use std::{
    ffi::OsStr,
    fs::{self, File, Metadata, OpenOptions},
    io,
    path::{Component, Path, PathBuf},
};

pub(crate) fn error(message: impl Into<String>) -> io::Error {
    io::Error::other(message.into())
}
pub(crate) fn absolute(path: &str) -> io::Result<PathBuf> {
    let mut result = PathBuf::new();
    let candidate = Path::new(path);
    // Standard Windows canonicalize/file pickers can produce an extended
    // drive alias. Normalize only when the meaning is preserved; the prefix
    // check below still rejects device namespaces and opaque extended paths.
    #[cfg(windows)]
    let candidate = dunce::simplified(candidate);
    if !candidate.is_absolute() || path.contains('\0') {
        return Err(error("Choose a fully qualified folder path."));
    }
    for component in candidate.components() {
        match component {
            Component::ParentDir | Component::CurDir => {
                return Err(error("Path traversal is not allowed."));
            }
            Component::Prefix(prefix) => {
                #[cfg(windows)]
                if !matches!(
                    prefix.kind(),
                    std::path::Prefix::Disk(_) | std::path::Prefix::UNC(_, _)
                ) {
                    return Err(error(
                        "Device or extended namespace paths are not accepted.",
                    ));
                }
                result.push(prefix.as_os_str());
            }
            _ => result.push(component.as_os_str()),
        }
    }
    #[cfg(target_os = "macos")]
    for alias in ["/tmp", "/var"] {
        if result.starts_with(alias)
            && fs::read_link(alias).ok().as_deref().is_some_and(|p| {
                p == Path::new(&format!("private{alias}"))
                    || p == Path::new(&format!("/private{alias}"))
            })
        {
            return Ok(Path::new("/private").join(
                result
                    .strip_prefix("/")
                    .map_err(|_| error("Invalid path."))?,
            ));
        }
    }
    Ok(result)
}
pub(crate) fn linked(md: &Metadata) -> bool {
    #[cfg(windows)]
    {
        use std::os::windows::fs::MetadataExt;
        md.file_type().is_symlink() || md.file_attributes() & 0x400 != 0
    }
    #[cfg(not(windows))]
    {
        md.file_type().is_symlink()
    }
}
pub(crate) fn regular(md: &Metadata) -> bool {
    md.is_file() && !linked(md)
}
fn name(path: &Path) -> io::Result<&OsStr> {
    path.file_name().ok_or_else(|| error("Missing file name."))
}

#[cfg(unix)]
mod platform {
    use super::*;
    use std::{
        ffi::CString,
        os::unix::{
            ffi::OsStrExt,
            io::{AsRawFd, FromRawFd},
        },
    };
    fn c(s: &OsStr) -> io::Result<CString> {
        CString::new(s.as_bytes()).map_err(|_| error("Invalid path component."))
    }
    fn file(fd: libc::c_int) -> io::Result<File> {
        if fd < 0 {
            Err(io::Error::last_os_error())
        } else {
            Ok(unsafe { File::from_raw_fd(fd) })
        }
    }
    const DIR: libc::c_int =
        libc::O_RDONLY | libc::O_NOFOLLOW | libc::O_CLOEXEC | libc::O_DIRECTORY;
    pub(super) fn directory(path: &Path, create: bool) -> io::Result<File> {
        let mut dir = file(unsafe { libc::open(c(OsStr::new("/"))?.as_ptr(), DIR) })?;
        for part in path.components() {
            let child = match part {
                Component::RootDir => continue,
                Component::Normal(child) => c(child)?,
                _ => return Err(error("Invalid directory component.")),
            };
            let mut next = file(unsafe { libc::openat(dir.as_raw_fd(), child.as_ptr(), DIR) });
            if create
                && next
                    .as_ref()
                    .is_err_and(|e| e.kind() == io::ErrorKind::NotFound)
            {
                let code = unsafe { libc::mkdirat(dir.as_raw_fd(), child.as_ptr(), 0o755) };
                if code != 0 && io::Error::last_os_error().kind() != io::ErrorKind::AlreadyExists {
                    return Err(io::Error::last_os_error());
                }
                next = file(unsafe { libc::openat(dir.as_raw_fd(), child.as_ptr(), DIR) });
            }
            dir = next?;
        }
        Ok(dir)
    }
    pub(super) fn source(dir: &File, name: &OsStr) -> io::Result<File> {
        let c = c(name)?;
        let reader = file(unsafe {
            libc::openat(
                dir.as_raw_fd(),
                c.as_ptr(),
                libc::O_RDONLY | libc::O_NOFOLLOW | libc::O_NONBLOCK | libc::O_CLOEXEC,
            )
        })?;
        if !regular(&reader.metadata()?) {
            return Err(error("Source is not an ordinary file."));
        }
        Ok(reader)
    }
    pub(super) fn stage(dir: &File, name: &OsStr) -> io::Result<File> {
        let c = c(name)?;
        file(unsafe {
            libc::openat(
                dir.as_raw_fd(),
                c.as_ptr(),
                libc::O_RDWR | libc::O_CREAT | libc::O_EXCL | libc::O_NOFOLLOW | libc::O_CLOEXEC,
                0o600,
            )
        })
    }
    pub(super) fn unlink(dir: &File, name: &OsStr) -> io::Result<()> {
        let c = c(name)?;
        if unsafe { libc::unlinkat(dir.as_raw_fd(), c.as_ptr(), 0) } == 0 {
            Ok(())
        } else {
            Err(io::Error::last_os_error())
        }
    }
    pub(super) fn publish(dir: &File, stage: &OsStr, target: &OsStr) -> io::Result<()> {
        let src = c(stage)?;
        let dst = c(target)?;
        let fd = dir.as_raw_fd();
        if unsafe { libc::linkat(fd, src.as_ptr(), fd, dst.as_ptr(), 0) } == 0 {
            return Ok(());
        }
        let error = io::Error::last_os_error();
        // Some removable filesystems do not support hard links. Use only a
        // kernel-enforced no-replace rename, never an unchecked plain rename.
        if error
            .raw_os_error()
            .is_some_and(|n| [libc::EOPNOTSUPP, libc::ENOSYS, libc::EPERM].contains(&n))
        {
            #[cfg(target_os = "macos")]
            {
                if unsafe {
                    libc::renameatx_np(fd, src.as_ptr(), fd, dst.as_ptr(), libc::RENAME_EXCL)
                } == 0
                {
                    return Ok(());
                }
                return Err(io::Error::last_os_error());
            }
            #[cfg(target_os = "linux")]
            {
                if unsafe {
                    libc::renameat2(fd, src.as_ptr(), fd, dst.as_ptr(), libc::RENAME_NOREPLACE)
                } == 0
                {
                    return Ok(());
                }
                return Err(io::Error::last_os_error());
            }
        }
        Err(error)
    }
}

#[cfg(not(unix))]
mod platform {
    use super::*;
    pub(super) fn directory(path: &Path, create: bool) -> io::Result<PathBuf> {
        let mut cursor = PathBuf::new();
        for part in path.components() {
            cursor.push(part.as_os_str());
            if matches!(part, Component::Prefix(_)) {
                continue;
            }
            let mut md = fs::symlink_metadata(&cursor);
            if create
                && md
                    .as_ref()
                    .is_err_and(|e| e.kind() == io::ErrorKind::NotFound)
            {
                match fs::create_dir(&cursor) {
                    Ok(()) => (),
                    Err(e) if e.kind() == io::ErrorKind::AlreadyExists => (),
                    Err(e) => return Err(e),
                };
                md = fs::symlink_metadata(&cursor);
            }
            let md = md?;
            if !md.is_dir() || linked(&md) {
                return Err(error("A directory is linked or not a directory."));
            }
        }
        Ok(path.to_owned())
    }
    pub(super) fn source(dir: &Path, name: &OsStr) -> io::Result<File> {
        let path = dir.join(name);
        if !regular(&fs::symlink_metadata(&path)?) {
            return Err(error("Source is not an ordinary file."));
        }
        let mut opts = OpenOptions::new();
        opts.read(true);
        #[cfg(windows)]
        {
            use std::os::windows::fs::OpenOptionsExt;
            opts.custom_flags(0x0020_0000);
        }
        let file = opts.open(&path)?;
        if !regular(&file.metadata()?) {
            return Err(error("Source is not an ordinary file."));
        }
        directory(dir, false)?;
        Ok(file)
    }
    pub(super) fn stage(dir: &Path, name: &OsStr) -> io::Result<File> {
        directory(dir, false)?;
        OpenOptions::new()
            .read(true)
            .write(true)
            .create_new(true)
            .open(dir.join(name))
    }
    pub(super) fn unlink(dir: &Path, name: &OsStr) -> io::Result<()> {
        directory(dir, false)?;
        fs::remove_file(dir.join(name))
    }
    pub(super) fn publish(dir: &Path, stage: &OsStr, target: &OsStr) -> io::Result<()> {
        directory(dir, false)?;
        let path = dir.join(stage);
        if !regular(&fs::symlink_metadata(&path)?) {
            return Err(error("Staged copy is not a regular file."));
        }
        // Prefer a hard link, with a Windows MoveFileW fallback for removable
        // volumes lacking hard links. With no replace flag it fails on collisions.
        match fs::hard_link(&path, dir.join(target)) {
            #[cfg(windows)]
            Err(e) if e.kind() != io::ErrorKind::AlreadyExists => {
                move_exclusive(&path, &dir.join(target))
            }
            result => result,
        }
    }
    #[cfg(windows)]
    fn move_exclusive(source: &Path, target: &Path) -> io::Result<()> {
        use std::os::windows::ffi::OsStrExt;
        #[link(name = "kernel32")]
        unsafe extern "system" {
            fn MoveFileW(old: *const u16, new: *const u16) -> i32;
        }
        let wide = |p: &Path| {
            p.as_os_str()
                .encode_wide()
                .chain(Some(0))
                .collect::<Vec<_>>()
        };
        let a = wide(source);
        let b = wide(target);
        if unsafe { MoveFileW(a.as_ptr(), b.as_ptr()) } != 0 {
            Ok(())
        } else {
            Err(io::Error::last_os_error())
        }
    }
}

pub(crate) fn directory(path: &Path, create: bool) -> io::Result<()> {
    platform::directory(path, create).map(drop)
}
pub(crate) fn open(path: &Path) -> io::Result<File> {
    let dir = platform::directory(
        path.parent()
            .ok_or_else(|| error("Missing parent folder."))?,
        false,
    )?;
    platform::source(&dir, name(path)?)
}
#[cfg(unix)]
type CopyDirectory = File;
#[cfg(not(unix))]
type CopyDirectory = PathBuf;

/// A copy which has not been published. Its staging name is private and
/// is cleaned up even if another worker fails or the coordinator stops.
pub(crate) struct StagedCopy {
    dir: CopyDirectory,
    destination: PathBuf,
    stage: String,
    #[cfg(unix)]
    identity: (u64, u64),
}
#[cfg(unix)]
fn identity(file: &File) -> io::Result<(u64, u64)> {
    use std::os::unix::fs::MetadataExt;
    let m = file.metadata()?;
    Ok((m.dev(), m.ino()))
}
impl StagedCopy {
    pub(crate) fn publish(self) -> io::Result<()> {
        let destination = &self.destination;
        let stage = OsStr::new(&self.stage);
        #[cfg(unix)]
        {
            let parent = destination
                .parent()
                .ok_or_else(|| error("Missing output folder."))?;
            let still = platform::directory(parent, false)?;
            if identity(&self.dir)? != identity(&still)? {
                return Err(error("Output folder changed during export."));
            }
            if identity(&platform::source(&self.dir, stage)?)? != self.identity {
                return Err(error("The staged copy changed before publishing."));
            }
        }
        platform::publish(&self.dir, stage, name(destination)?)
    }
}
impl Drop for StagedCopy {
    fn drop(&mut self) {
        let stage = OsStr::new(&self.stage);
        #[cfg(unix)]
        if platform::source(&self.dir, stage)
            .and_then(|f| identity(&f))
            .ok()
            != Some(self.identity)
        {
            return;
        }
        let _ = platform::unlink(&self.dir, stage);
    }
}
pub(crate) fn stage_copy<T>(
    path: &Path,
    write: impl FnOnce(&mut File) -> io::Result<T>,
) -> io::Result<(StagedCopy, T)> {
    let parent = path
        .parent()
        .ok_or_else(|| error("Missing output folder."))?;
    let dir = platform::directory(parent, true)?;
    let stage = format!(".music-sorter-{}.partial", uuid::Uuid::new_v4());
    let mut file = platform::stage(&dir, OsStr::new(&stage))?;
    let pending = StagedCopy {
        dir,
        destination: path.to_owned(),
        stage,
        #[cfg(unix)]
        identity: identity(&file)?,
    };
    let outcome = write(&mut file).and_then(|value| {
        file.sync_all()?;
        Ok(value)
    });
    drop(file);
    outcome.map(|value| (pending, value))
}
pub(crate) fn state_lock(path: &Path) -> io::Result<File> {
    directory(
        path.parent()
            .ok_or_else(|| error("Missing state folder."))?,
        true,
    )?;
    match fs::symlink_metadata(path) {
        Ok(m) if !regular(&m) => return Err(error("State lock is not an ordinary file.")),
        Ok(_) => (),
        Err(e) if e.kind() == io::ErrorKind::NotFound => (),
        Err(e) => return Err(e),
    }
    let mut opts = OpenOptions::new();
    opts.read(true).write(true).create(true).truncate(false);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        opts.mode(0o600).custom_flags(libc::O_NOFOLLOW);
    }
    #[cfg(windows)]
    {
        use std::os::windows::fs::OpenOptionsExt;
        opts.custom_flags(0x0020_0000);
    }
    let f = opts.open(path)?;
    if !regular(&f.metadata()?) {
        return Err(error("State lock is not an ordinary file."));
    }
    f.try_lock()
        .map_err(|_| error("Another Music Sorter operation currently owns this state lock."))?;
    Ok(f)
}
pub(crate) fn replace_state(path: &Path, bytes: &[u8]) -> io::Result<()> {
    use std::io::Write;
    let parent = path
        .parent()
        .ok_or_else(|| error("Missing state folder."))?;
    directory(parent, true)?;
    match fs::symlink_metadata(path) {
        Ok(m) if !regular(&m) => return Err(error("State file is not an ordinary file.")),
        Ok(_) => (),
        Err(e) if e.kind() == io::ErrorKind::NotFound => (),
        Err(e) => return Err(e),
    }
    let stage = parent.join(format!(
        ".music-sorter-state-{}.partial",
        uuid::Uuid::new_v4()
    ));
    let mut opts = OpenOptions::new();
    opts.write(true).create_new(true);
    #[cfg(unix)]
    {
        use std::os::unix::fs::OpenOptionsExt;
        opts.mode(0o600);
    }
    let mut owned = false;
    let result = (|| {
        let mut f = opts.open(&stage)?;
        owned = true;
        f.write_all(bytes)?;
        f.sync_all()?;
        drop(f);
        directory(parent, false)?;
        fs::rename(&stage, path)
    })();
    if result.is_err() && owned {
        let _ = fs::remove_file(stage);
    }
    result
}
