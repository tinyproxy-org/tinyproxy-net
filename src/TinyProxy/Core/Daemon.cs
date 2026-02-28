using System.Runtime.InteropServices;

namespace TinyProxy.Core;

/// <summary>
/// POSIX daemon process support.
/// Aligns with tinyproxy C's daemon.c functionality.
/// </summary>
public static class Daemon
{
    /// <summary>
    /// Makes the calling process a daemon.
    /// Forks twice to ensure the process is not a session leader,
    /// calls setsid() to create a new session, and closes standard file descriptors.
    /// Aligns with tinyproxy C's makedaemon() function.
    /// </summary>
    public static void MakeDaemon()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) &&
            !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            throw new PlatformNotSupportedException("Daemon mode is only supported on Unix-like systems");

        // First fork: parent exits, child continues
        if (Fork() != 0) Environment.Exit(0);

        // Create new session
        Setsid();

        // Ignore SIGHUP (terminal hangup)
        IgnoreSignal(Signal.SIGHUP);

        // Second fork: ensure we're not a session leader
        if (Fork() != 0) Environment.Exit(0);

        // Change working directory to root
        try
        {
            Directory.SetCurrentDirectory("/");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: Could not change directory to /: {ex.Message}");
        }

        // Set umask to 0177 (rw-------)
        // In .NET, we don't have direct umask control, but we can ensure
        // created files have restrictive permissions

        // Close standard file descriptors
        // Note: In debug builds, keep them open for logging
#if !DEBUG
        try
        {
            // Redirect stdin from /dev/null
            var devNull = UnixOpen("/dev/null", UnixOpenFlags.O_RDWR);
            if (devNull >= 0)
            {
                var stdin = new UnixStream(devNull, true);
                Console.SetIn(new StreamReader(stdin));
            }

            // Redirect stdout and stderr to /dev/null
            var devNullOut = UnixOpen("/dev/null", UnixOpenFlags.O_RDWR);
            if (devNullOut >= 0)
            {
                var stdout = new UnixStream(devNullOut, true);
                Console.SetOut(new StreamWriter(stdout));
            }

            var devNullErr = UnixOpen("/dev/null", UnixOpenFlags.O_RDWR);
            if (devNullErr >= 0)
            {
                var stderr = new UnixStream(devNullErr, true);
                Console.SetError(new StreamWriter(stderr));
            }
        }
        catch
        {
            // Ignore errors in debug builds
        }
#endif
    }

    /// <summary>
    /// Forks the current process.
    /// Returns 0 in child process, child PID in parent, or -1 on error.
    /// </summary>
    private static int Fork()
    {
        return fork();
    }

    /// <summary>
    /// Creates a new session and sets the process group ID.
    /// Returns the new session ID.
    /// </summary>
    private static int Setsid()
    {
        return setsid();
    }

    /// <summary>
    /// Sets a signal handler.
    /// Aligns with tinyproxy C's set_signal_handler() function.
    /// </summary>
    public static void SetSignalHandler(Signal signal, SignalHandler handler)
    {
        // .NET doesn't provide direct signal handling.
        // Use AppDomain.ProcessExit and Console.CancelKeyPress instead.
        switch (signal)
        {
            case Signal.SIGTERM:
                AppDomain.CurrentDomain.ProcessExit += (s, e) => handler();
                break;
            case Signal.SIGINT:
                Console.CancelKeyPress += (s, e) =>
                {
                    handler();
                    e.Cancel = true;
                };
                break;
            case Signal.SIGHUP:
                // Config reload is handled by ConfigReloader via file watching
                break;
        }
    }

    /// <summary>
    /// Ignores a signal by setting its handler to no-op.
    /// </summary>
    private static void IgnoreSignal(Signal signal)
    {
        SetSignalHandler(signal, () => { });
    }

    // Platform invoke declarations for Unix functions
    [DllImport("libc", SetLastError = true)]
    private static extern int fork();

    [DllImport("libc", SetLastError = true)]
    private static extern int setsid();

    [DllImport("libc", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int unix_open(string pathname, int flags);

    private static int UnixOpen(string pathname, UnixOpenFlags flags)
    {
        try
        {
            return unix_open(pathname, (int)flags);
        }
        catch
        {
            return -1;
        }
    }

    // UnixStream for wrapping file descriptors
    private sealed class UnixStream : Stream
    {
        private readonly int _fd;
        private readonly bool _ownsFd;

        public UnixStream(int fd, bool ownsFd)
        {
            _fd = fd;
            _ownsFd = ownsFd;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
            // No-op for file descriptors
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_fd < 0) throw new ObjectDisposedException(nameof(UnixStream));
            var n = unix_read(_fd, buffer, offset, count);
            return n;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_fd < 0) throw new ObjectDisposedException(nameof(UnixStream));
            unix_write(_fd, buffer, offset, count);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        protected override void Dispose(bool disposing)
        {
            if (_ownsFd && _fd >= 0) unix_close(_fd);
            base.Dispose(disposing);
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int unix_read(int fd, byte[] buf, int offset, int count);

        [DllImport("libc", SetLastError = true)]
        private static extern void unix_write(int fd, byte[] buf, int offset, int count);

        [DllImport("libc", SetLastError = true)]
        private static extern int unix_close(int fd);
    }

    /// <summary>
    /// Signal numbers.
    /// Aligns with tinyproxy C's signal handling.
    /// </summary>
    public enum Signal
    {
        SIGHUP = 1,
        SIGINT = 2,
        SIGQUIT = 3,
        SIGILL = 4,
        SIGTRAP = 5,
        SIGABRT = 6,
        SIGBUS = 7,
        SIGFPE = 8,
        SIGKILL = 9,
        SIGUSR1 = 10,
        SIGSEGV = 11,
        SIGUSR2 = 12,
        SIGPIPE = 13,
        SIGALRM = 14,
        SIGTERM = 15,
        SIGCHLD = 17,
        SIGCONT = 18,
        SIGSTOP = 19,
        SIGTSTP = 20,
        SIGTTIN = 21,
        SIGTTOU = 22
    }

    /// <summary>
    /// Signal handler delegate.
    /// </summary>
    public delegate void SignalHandler();

    /// <summary>
    /// Open flags for Unix open() system call.
    /// Using hex values instead of octal for compatibility.
    /// </summary>
    [Flags]
    private enum UnixOpenFlags
    {
        O_RDONLY = 0x000000,
        O_WRONLY = 0x000001,
        O_RDWR = 0x000002,
        O_CREAT = 0x000100,
        O_EXCL = 0x000200,
        O_NOCTTY = 0x000400,
        O_TRUNC = 0x001000,
        O_APPEND = 0x002000,
        O_NONBLOCK = 0x004000,
        O_SYNC = 0x010000
    }
}