using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;
using ComStat = System.Runtime.InteropServices.ComTypes.STATSTG;

namespace S3Explorer.App;

public sealed record ShellClipboardFile(string RelativePath, bool IsDirectory, long Length, DateTimeOffset? LastModified);

// Explicit HRESULT and Win32 BOOL signatures are important: the Shell invokes this
// interface directly, rather than through WinForms' IDataObject wrapper.
[ComVisible(true), Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDataObjectAsyncCapability
{
    [PreserveSig] int SetAsyncMode([MarshalAs(UnmanagedType.Bool)] bool enabled);
    [PreserveSig] int GetAsyncMode([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [PreserveSig] int StartOperation(IntPtr reserved);
    [PreserveSig] int InOperation([MarshalAs(UnmanagedType.Bool)] out bool inOperation);
    [PreserveSig] int EndOperation(int result, IntPtr reserved, uint effects);
}

// A native IStream ABI, including S_FALSE for a short read. Do not use automatic
// class interfaces or marshal Read's buffer into a file-sized managed array.
[ComVisible(true), Guid("0000000C-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IShellReadStream
{
    [PreserveSig] int Read(IntPtr buffer, uint count, IntPtr bytesRead);
    [PreserveSig] int Write(IntPtr buffer, uint count, IntPtr bytesWritten);
    [PreserveSig] int Seek(long move, uint origin, IntPtr newPosition);
    [PreserveSig] int SetSize(ulong size);
    [PreserveSig] int CopyTo(IShellReadStream target, ulong count, IntPtr bytesRead, IntPtr bytesWritten);
    [PreserveSig] int Commit(uint flags);
    [PreserveSig] int Revert();
    [PreserveSig] int LockRegion(ulong offset, ulong count, uint type);
    [PreserveSig] int UnlockRegion(ulong offset, ulong count, uint type);
    [PreserveSig] int Stat(out ComStat stat, uint flags);
    [PreserveSig] int Clone(out IShellReadStream stream);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellFileDataObject : ComDataObject, IDataObjectAsyncCapability, IDisposable
{
    public const string TokenFormatName = "S3Explorer.FileClipboard.Token.v1";
    internal const int FormatError = unchecked((int)0x80040064);
    internal const int IndexError = unchecked((int)0x80040068);
    internal const int TymedError = unchecked((int)0x80040069);
    internal const int AspectError = unchecked((int)0x8004006B);
    internal const int AccessDenied = unchecked((int)0x80030005);
    internal const int ReadFault = unchecked((int)0x8003001E);
    private const int NotImplemented = unchecked((int)0x80004001);
    private readonly ShellClipboardFile[] files;
    private readonly Func<int, Stream> openRead;
    private Action? release;
    private int disposed;
    private int asyncMode = 1;
    private int operation;
    private readonly short descriptorFormat = Register("FileGroupDescriptorW");
    private readonly short contentsFormat = Register("FileContents");
    private readonly short effectFormat = Register("Preferred DropEffect");
    private readonly short tokenFormat = Register(TokenFormatName);
    private readonly byte[] descriptorBytes;
    private readonly byte[] tokenBytes;

    public ShellFileDataObject(IReadOnlyList<ShellClipboardFile> files, Func<int, Stream> openRead, string token, Action? release = null)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(openRead);
        ArgumentException.ThrowIfNullOrEmpty(token);
        this.files = files.ToArray();
        this.openRead = openRead;
        this.release = release;
        descriptorBytes = BuildFileGroupDescriptor(this.files);
        tokenBytes = Encoding.Unicode.GetBytes(token + "\0");
    }

    // Only explicit cancellation/application shutdown disposes a live object.
    // Ordinary clipboard replacement merely drops the publisher's root. Native
    // references to the CCW and every outstanding stream retain this owner.
    public void Dispose() { Interlocked.Exchange(ref disposed, 1); ReleaseResources(); GC.SuppressFinalize(this); }
    ~ShellFileDataObject() { ReleaseResources(); }
    private void ReleaseResources() { try { Interlocked.Exchange(ref release, null)?.Invoke(); } catch { /* Finalizers must not escape. */ } }
    internal void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
    internal Stream OpenRead(int index) { ThrowIfDisposed(); return openRead(index); }

    internal static byte[] BuildFileGroupDescriptor(IReadOnlyList<ShellClipboardFile> entries)
    {
        // FILEDESCRIPTORW is 592 bytes, including the fixed WCHAR[260] name.
        var bytes = new byte[checked(4 + entries.Count * 592)];
        using var writer = new BinaryWriter(new MemoryStream(bytes), Encoding.Unicode);
        writer.Write(entries.Count);
        foreach (var entry in entries)
        {
            var name = entry.RelativePath.Replace('/', '\\');
            if (string.IsNullOrWhiteSpace(name) || name.Length >= 260 || name.StartsWith('\\') ||
                name.Split('\\').Any(p => p is "" or "." or ".." || p.IndexOfAny(['\0', ':', '*', '?', '"', '<', '>', '|']) >= 0) || entry.Length < 0)
                throw new ArgumentException("Invalid Windows clipboard relative path or length.", nameof(entries));
            long start = writer.BaseStream.Position;
            uint flags = 0x4 | 0x80000000; // FD_ATTRIBUTES | FD_UNICODE
            // Do not advertise a known zero size: Shell is allowed to create such a file from
            // descriptors alone. Requesting FileContents lets us validate the empty snapshot.
            if (!entry.IsDirectory && entry.Length > 0) flags |= 0x40; // FD_FILESIZE
            if (entry.LastModified.HasValue) flags |= 0x20; // FD_WRITESTIME
            writer.Write(flags);
            writer.BaseStream.Position = start + 36;
            writer.Write(entry.IsDirectory ? 0x10u : 0x80u); // DIRECTORY / NORMAL
            writer.BaseStream.Position = start + 56;
            writer.Write(entry.LastModified?.UtcDateTime.ToFileTimeUtc() ?? 0L);
            writer.Write((uint)((ulong)entry.Length >> 32));
            writer.Write((uint)entry.Length);
            writer.Write(Encoding.Unicode.GetBytes(name));
            writer.BaseStream.Position = start + 592;
        }
        return bytes;
    }

    public int QueryGetData(ref FORMATETC format)
    {
        if (format.dwAspect != DVASPECT.DVASPECT_CONTENT) return AspectError;
        if (format.ptd != IntPtr.Zero) return unchecked((int)0x80040065); // DV_E_DVTARGETDEVICE
        bool contents = format.cfFormat == contentsFormat;
        if (files.Length == 0 && (contents || format.cfFormat == descriptorFormat)) return FormatError;
        if (!contents && format.cfFormat != descriptorFormat && format.cfFormat != effectFormat && format.cfFormat != tokenFormat) return FormatError;
        if (contents ? format.lindex < 0 || format.lindex >= files.Length || files[format.lindex].IsDirectory : format.lindex != -1) return IndexError;
        return (format.tymed & (contents ? TYMED.TYMED_ISTREAM : TYMED.TYMED_HGLOBAL)) == 0 ? TymedError : 0;
    }

    public void GetData(ref FORMATETC format, out STGMEDIUM medium)
    {
        medium = default;
        ThrowIfDisposed();
        Marshal.ThrowExceptionForHR(QueryGetData(ref format));
        if (format.cfFormat == contentsFormat)
        {
            // Zero-length consumers may use Stat and never call Read. A FileContents request
            // still validates the remote snapshot; descriptor discovery remains download-free.
            if (files[format.lindex].Length == 0)
            {
                using var empty = OpenRead(format.lindex);
                if (empty.Length != 0) throw new COMException("Empty clipboard object changed.", ReadFault);
            }
            var stream = new ShellFileReadStream(this, format.lindex, files[format.lindex]);
            medium.tymed = TYMED.TYMED_ISTREAM;
            medium.unionmember = Marshal.GetComInterfaceForObject(stream, typeof(IShellReadStream));
            // The caller owns this +1 and releases it via ReleaseStgMedium.
            return;
        }
        byte[] bytes = format.cfFormat == descriptorFormat ? descriptorBytes : format.cfFormat == tokenFormat ? tokenBytes : BitConverter.GetBytes(1u);
        IntPtr handle = GlobalAlloc(0x42, (nuint)bytes.Length); // MOVEABLE | ZEROINIT
        if (handle == IntPtr.Zero) throw new OutOfMemoryException();
        IntPtr pointer = GlobalLock(handle);
        if (pointer == IntPtr.Zero) { GlobalFree(handle); throw new OutOfMemoryException(); }
        try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
        catch { GlobalUnlock(handle); GlobalFree(handle); throw; }
        GlobalUnlock(handle);
        medium.tymed = TYMED.TYMED_HGLOBAL;
        medium.unionmember = handle;
    }
    public void GetDataHere(ref FORMATETC format, ref STGMEDIUM medium) => throw new COMException("GetDataHere is unsupported.", TymedError);
    public int GetCanonicalFormatEtc(ref FORMATETC input, out FORMATETC output) { output = input; output.ptd = IntPtr.Zero; return 0x40130; }
    public void SetData(ref FORMATETC format, ref STGMEDIUM medium, bool release) => throw new COMException("Read-only clipboard.", NotImplemented);
    public IEnumFORMATETC EnumFormatEtc(DATADIR direction)
    {
        if (direction != DATADIR.DATADIR_GET) throw new COMException("Read-only clipboard.", NotImplemented);
        var formats = new List<FORMATETC> { Format(effectFormat, -1, TYMED.TYMED_HGLOBAL), Format(tokenFormat, -1, TYMED.TYMED_HGLOBAL) };
        if (files.Length > 0) formats.Insert(0, Format(descriptorFormat, -1, TYMED.TYMED_HGLOBAL));
        for (int i = 0; i < files.Length; i++) if (!files[i].IsDirectory) formats.Add(Format(contentsFormat, i, TYMED.TYMED_ISTREAM));
        return new ShellFormatEnumerator(formats.ToArray());
    }
    private static FORMATETC Format(short id, int index, TYMED tymed) => new() { cfFormat = id, dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = tymed };
    public int DAdvise(ref FORMATETC format, ADVF flags, IAdviseSink sink, out int connection) { connection = 0; return unchecked((int)0x80040003); }
    public void DUnadvise(int connection) => throw new COMException("Advisory connections unsupported.", unchecked((int)0x80040003));
    public int EnumDAdvise(out IEnumSTATDATA enumAdvise) { enumAdvise = null!; return unchecked((int)0x80040003); }
    public int SetAsyncMode(bool enabled) { Volatile.Write(ref asyncMode, enabled ? 1 : 0); return 0; }
    public int GetAsyncMode(out bool enabled) { enabled = Volatile.Read(ref asyncMode) != 0; return 0; }
    public int StartOperation(IntPtr reserved) { Volatile.Write(ref operation, 1); return 0; }
    public int InOperation(out bool inOperation) { inOperation = Volatile.Read(ref operation) != 0; return 0; }
    public int EndOperation(int result, IntPtr reserved, uint effects) { Volatile.Write(ref operation, 0); return 0; }
    private static short Register(string name) { uint id = RegisterClipboardFormat(name); if (id == 0) throw new System.ComponentModel.Win32Exception(); return unchecked((short)id); }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint RegisterClipboardFormat(string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalFree(IntPtr handle);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellFormatEnumerator : IEnumFORMATETC
{
    private readonly FORMATETC[] formats;
    private int position;
    internal ShellFormatEnumerator(FORMATETC[] formats, int position = 0) { this.formats = formats; this.position = position; }
    public int Next(int count, FORMATETC[] items, int[]? fetched)
    {
        if (count < 0 || items.Length < count || (count != 1 && (fetched is null || fetched.Length == 0))) return unchecked((int)0x80070057);
        int copied = 0;
        while (copied < count && position < formats.Length) items[copied++] = formats[position++];
        if (fetched is { Length: > 0 }) fetched[0] = copied;
        return copied == count ? 0 : 1;
    }
    public int Skip(int count) { if (count < 0) return unchecked((int)0x80070057); int skipped = Math.Min(count, formats.Length - position); position += skipped; return skipped == count ? 0 : 1; }
    public int Reset() { position = 0; return 0; }
    public void Clone(out IEnumFORMATETC clone) => clone = new ShellFormatEnumerator(formats, position);
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ShellFileReadStream : IShellReadStream
{
    private readonly ShellFileDataObject owner;
    private readonly int index;
    private readonly ShellClipboardFile file;
    private readonly object gate = new();
    private Stream? source;
    private long position;
    internal ShellFileReadStream(ShellFileDataObject owner, int index, ShellClipboardFile file, long position = 0) { this.owner = owner; this.index = index; this.file = file; this.position = position; }
    ~ShellFileReadStream() { try { source?.Dispose(); } catch { } }
    public int Read(IntPtr buffer, uint count, IntPtr bytesRead)
    {
        if (bytesRead != IntPtr.Zero) Marshal.WriteInt32(bytesRead, 0);
        if (count > 0 && buffer == IntPtr.Zero) return unchecked((int)0x80030009);
        lock (gate)
        {
            uint total = 0;
            try
            {
                owner.ThrowIfDisposed();
                if (count == 0) return 0;
                if (position >= file.Length)
                {
                    // Empty objects still require identity/permission validation
                    // on the first real read, even though there are no bytes.
                    if (position == 0 && file.Length == 0) source ??= owner.OpenRead(index);
                    return 1;
                }
                source ??= owner.OpenRead(index);
                if (!source.CanRead || !source.CanSeek) throw new IOException("Clipboard source must be readable and seekable.");
                source.Position = position;
                var chunk = new byte[(int)Math.Min(count, 64 * 1024u)];
                while (total < count && position < file.Length)
                {
                    owner.ThrowIfDisposed();
                    int wanted = (int)Math.Min(Math.Min((long)(count - total), chunk.Length), file.Length - position);
                    int read = source.Read(chunk, 0, wanted);
                    if (read == 0) throw new IOException("Clipboard source ended before its declared length.");
                    Marshal.Copy(chunk, 0, new IntPtr(checked(buffer.ToInt64() + total)), read);
                    total += (uint)read;
                    position += read;
                    if (bytesRead != IntPtr.Zero) Marshal.WriteInt32(bytesRead, unchecked((int)total));
                }
                return total == count ? 0 : 1;
            }
            catch (Exception ex) { return Error(ex); }
        }
    }
    public int Seek(long move, uint origin, IntPtr newPosition)
    {
        lock (gate)
        {
            try
            {
                owner.ThrowIfDisposed();
                long next = checked((origin switch { 0 => 0L, 1 => position, 2 => file.Length, _ => throw new ArgumentException() }) + move);
                if (next < 0) return unchecked((int)0x80030001);
                position = next;
                if (newPosition != IntPtr.Zero) Marshal.WriteInt64(newPosition, position);
                return 0;
            }
            catch (ArgumentException) { return unchecked((int)0x80030001); }
            catch (OverflowException) { return unchecked((int)0x80030001); }
            catch (Exception ex) { return Error(ex); }
        }
    }
    public int Stat(out ComStat stat, uint flags)
    {
        stat = default;
        try
        {
            owner.ThrowIfDisposed();
            if ((flags & ~3u) != 0) return unchecked((int)0x800300FF);
            // STATFLAG_NONAME requires a native null string despite STATSTG's non-null annotation.
            stat = new ComStat { type = 2, cbSize = file.Length, grfMode = 0, pwcsName = (flags & 1) != 0 ? null! : file.RelativePath };
            if (file.LastModified is { } modified) { long time = modified.UtcDateTime.ToFileTimeUtc(); stat.mtime = new FILETIME { dwLowDateTime = (int)time, dwHighDateTime = (int)(time >> 32) }; }
            return 0;
        }
        catch (Exception ex) { return Error(ex); }
    }
    public int Clone(out IShellReadStream stream)
    {
        stream = null!;
        try { lock (gate) { owner.ThrowIfDisposed(); stream = new ShellFileReadStream(owner, index, file, position); } return 0; }
        catch (Exception ex) { return Error(ex); }
    }
    public int CopyTo(IShellReadStream target, ulong count, IntPtr bytesRead, IntPtr bytesWritten)
    {
        if (bytesRead != IntPtr.Zero) Marshal.WriteInt64(bytesRead, 0);
        if (bytesWritten != IntPtr.Zero) Marshal.WriteInt64(bytesWritten, 0);
        if (target is null) return unchecked((int)0x80030009);
        IntPtr buffer = Marshal.AllocHGlobal(64 * 1024), counters = Marshal.AllocHGlobal(8);
        ulong totalRead = 0, totalWritten = 0;
        try
        {
            while (totalRead < count)
            {
                uint requested = (uint)Math.Min(64 * 1024UL, count - totalRead);
                int result = Read(buffer, requested, counters);
                uint got = unchecked((uint)Marshal.ReadInt32(counters));
                totalRead += got;
                if (bytesRead != IntPtr.Zero) Marshal.WriteInt64(bytesRead, unchecked((long)totalRead));
                if (result < 0) return result;
                if (got > 0)
                {
                    Marshal.WriteInt32(counters, 0);
                    int writeResult = target.Write(buffer, got, counters);
                    uint written = unchecked((uint)Marshal.ReadInt32(counters));
                    totalWritten += written;
                    if (bytesWritten != IntPtr.Zero) Marshal.WriteInt64(bytesWritten, unchecked((long)totalWritten));
                    if (writeResult < 0) return writeResult;
                    if (written != got) return unchecked((int)0x8003001D);
                }
                if (result == 1 || got == 0) return 1;
            }
            return 0;
        }
        catch (Exception ex) { return Error(ex); }
        finally { Marshal.FreeHGlobal(buffer); Marshal.FreeHGlobal(counters); }
    }
    public int Write(IntPtr buffer, uint count, IntPtr bytesWritten) { if (bytesWritten != IntPtr.Zero) Marshal.WriteInt32(bytesWritten, 0); return ShellFileDataObject.AccessDenied; }
    public int SetSize(ulong size) => ShellFileDataObject.AccessDenied;
    public int Commit(uint flags) => ShellFileDataObject.AccessDenied;
    public int Revert() => ShellFileDataObject.AccessDenied;
    public int LockRegion(ulong offset, ulong count, uint type) => unchecked((int)0x80030001);
    public int UnlockRegion(ulong offset, ulong count, uint type) => unchecked((int)0x80030001);
    private static int Error(Exception ex) => ex switch
    {
        OperationCanceledException or ObjectDisposedException => unchecked((int)0x800704C7),
        UnauthorizedAccessException => ShellFileDataObject.AccessDenied,
        FileNotFoundException => unchecked((int)0x80030002),
        _ => ShellFileDataObject.ReadFault
    };
}

/// <summary>Publishes without tying Shell RPC/network reads to the app's UI STA.</summary>
public static class ShellClipboardPublisher
{
    private static readonly Lazy<Task<Control>> Dispatcher = new(StartDispatcher);
    private sealed record Publication(ShellFileDataObject Data, IntPtr OwnerWindow);
    private static Publication? current;
    public static async Task SetAsync(ShellFileDataObject data, CancellationToken cancellationToken = default, ShellFileDataObject? expected = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        cancellationToken.ThrowIfCancellationRequested();
        Control control = await Dispatcher.Value.ConfigureAwait(false);
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await PublishOnceAsync(control, data, cancellationToken, expected).ConfigureAwait(false);
                return;
            }
            catch (COMException ex) when (attempt < 10 &&
                (ex.HResult == unchecked((int)0x800401D0) || ex.HResult == unchecked((int)0x800401D2)))
            {
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Task PublishOnceAsync(Control control, ShellFileDataObject data, CancellationToken cancellationToken, ShellFileDataObject? expected)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.BeginInvoke((Action)(() =>
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                data.ThrowIfDisposed();
                if (expected is not null && OleIsCurrentClipboard(expected) != 0)
                    throw new OperationCanceledException("Clipboard ownership changed before export was ready.", cancellationToken);
                Marshal.ThrowExceptionForHR(OleSetClipboard(data));
                var ownerWindow = GetClipboardOwner();
                if (ownerWindow == IntPtr.Zero || OleIsCurrentClipboard(data) != 0)
                    throw new OperationCanceledException("Clipboard ownership changed during publication.", cancellationToken);
                Volatile.Write(ref current, new Publication(data, ownerWindow));
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }));
        return completion.Task;
    }
    // OleIsCurrentClipboard must be checked in the publishing apartment. Cross-STA
    // callers use the native owner HWND plus our immutable publication identity instead
    // of re-marshalling the CCW or blocking the UI on the publisher's message pump.
    // All replacement/clear operations still use OleIsCurrentClipboard on its own STA.
    public static bool IsCurrent(ShellFileDataObject data)
    {
        var publication = Volatile.Read(ref current);
        return publication is not null && ReferenceEquals(publication.Data, data) &&
            publication.OwnerWindow != IntPtr.Zero && GetClipboardOwner() == publication.OwnerWindow;
    }

    public static async Task ClearAsync(ShellFileDataObject? expected)
    {
        if (!Dispatcher.IsValueCreated) return;
        Control control = await Dispatcher.Value.ConfigureAwait(false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        control.BeginInvoke((Action)(() =>
        {
            try
            {
                var candidate = expected ?? current?.Data;
                if (candidate is not null && OleIsCurrentClipboard(candidate) == 0)
                {
                    Marshal.ThrowExceptionForHR(OleSetClipboard(null));
                    if (ReferenceEquals(candidate, current?.Data)) Volatile.Write(ref current, null);
                }
                completion.SetResult();
            }
            catch (Exception ex) { completion.SetException(ex); }
        }));
        await completion.Task.ConfigureAwait(false);
    }

    private static Task<Control> StartDispatcher()
    {
        var ready = new TaskCompletionSource<Control>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            int initialized = OleInitialize(IntPtr.Zero);
            if (initialized < 0) { ready.SetException(Marshal.GetExceptionForHR(initialized)!); return; }
            try
            {
                using var control = new Control();
                _ = control.Handle;
                using var timer = new System.Windows.Forms.Timer { Interval = 1000 };
                timer.Tick += (_, _) => { if (current is { } publication && OleIsCurrentClipboard(publication.Data) == 1) Volatile.Write(ref current, null); };
                timer.Start();
                ready.SetResult(control);
                Application.Run();
            }
            catch (Exception ex) { ready.TrySetException(ex); }
            finally { current = null; OleUninitialize(); }
        }) { IsBackground = true, Name = "S3 clipboard STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }
    [DllImport("ole32.dll")] private static extern int OleInitialize(IntPtr reserved);
    [DllImport("ole32.dll")] private static extern void OleUninitialize();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardOwner();
    [DllImport("ole32.dll")] private static extern int OleSetClipboard([MarshalAs(UnmanagedType.Interface)] ComDataObject? data);
    [DllImport("ole32.dll")] private static extern int OleIsCurrentClipboard([MarshalAs(UnmanagedType.Interface)] ComDataObject data);
}
