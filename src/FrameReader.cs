using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CompressForDiscord;

/// <summary>
/// Preview frames from a video that's kept open, using Windows' own decoders (Media Foundation). Starting ffmpeg
/// for every frame means reading the file's index each time, which for a long recording is most of the wait;
/// this reads it once. Moving forward a little decodes on from where it is instead of seeking again, and while
/// dragging, the nearest keyframe is enough. Files Windows can't read (or that are rotated) use ffmpeg instead.
/// Calls must not overlap; the window makes them one at a time, off the UI thread.
/// </summary>
sealed class FrameReader : IDisposable
{
    IMFSourceReader? reader;
    Size size;
    int stride;
    /// <summary>Where the decoder is: the end of the last frame it gave, in seconds; NaN = needs a seek</summary>
    double readPos = double.NaN;
    /// <summary>The last frame handed out and the time it covers, so asking again for the same frame is free</summary>
    Bitmap? last;
    double lastFrom = double.NaN, lastTo = double.NaN;
    readonly double frameLength;
    /// <summary>How long Windows thinks the video is (seeking past it is an error), or infinity if it won't say</summary>
    readonly double duration = double.PositiveInfinity;

    FrameReader(IMFSourceReader reader, double fps)
    {
        this.reader = reader;
        frameLength = fps > 0 ? 1 / fps : 1 / 30.0;
        var key = MF_PD_DURATION;
        if (reader.GetPresentationAttribute(MediaSource, ref key, out var d) >= 0 && d.vt == 21 && d.hVal > 0) duration = d.hVal / 1e7;
    }

    const uint MediaSource = 0xFFFFFFFF;
    static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");

    static readonly Lazy<bool> started = new(() => MFStartup(0x00020070, 0) >= 0);

    /// <summary>Whether frames are decoded on the graphics card (false if it couldn't be set up)</summary>
    public bool Gpu { get; private set; }

    /// <summary>The video opened for reading frames, or null if Windows can't (the caller uses ffmpeg then)</summary>
    public static FrameReader? Open(string path, double fps, bool gpu = true)
    {
        try
        {
            if (!started.Value) return null;
            Check(MFCreateAttributes(out var attrs, 4));
            // Lets the reader convert to RGB and resize, so frames come out ready to show
            var (avp, lowLatency) = (MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING, MF_LOW_LATENCY);
            Check(attrs.SetUINT32(ref avp, 1));
            // Hand each frame out as soon as it's decoded rather than holding a few back
            Check(attrs.SetUINT32(ref lowLatency, 1));
            var onGpu = gpu && UseGpu(attrs);
            Check(MFCreateSourceReaderFromURL(path, attrs, out var r));
            Marshal.ReleaseComObject(attrs);
            Check(r.SetStreamSelection(AllStreams, false));
            Check(r.SetStreamSelection(FirstVideo, true));
            Check(r.GetNativeMediaType(FirstVideo, 0, out var native));
            var rot = MF_MT_VIDEO_ROTATION;
            var rotated = native.GetUINT32(ref rot, out var degrees) >= 0 && degrees != 0;
            Marshal.ReleaseComObject(native);
            if (rotated)
            {
                Marshal.ReleaseComObject(r);
                return null;
            }
            return new FrameReader(r, fps) { Gpu = onGpu };
        }
        catch (Exception ex)
        {
            AppLog.Info($"Windows can't read this video's frames ({ex.Message}); using ffmpeg for the preview");
            return null;
        }
    }

    /// <summary>
    /// The frame showing at time t, at this size. Not exact: the keyframe at or before t if getting there means
    /// a seek (good enough while dragging, and much quicker). Null if cancelled or it couldn't be read.
    /// </summary>
    public Bitmap? Get(double t, Size want, bool exact, CancellationToken ct)
    {
        lock (gate) return GetLocked(t, want, exact, ct);
    }

    readonly object gate = new();

    Bitmap? GetLocked(double t, Size want, bool exact, CancellationToken ct)
    {
        if (reader == null) return null;
        if (want != size) SetSize(want);
        // At or past the end: the last frame
        t = Math.Max(0, Math.Min(t, duration - frameLength));
        if (last != null && t >= lastFrom - 1e-6 && t < lastTo - 1e-6) return new Bitmap(last);
        // Forward a little from where the decoder is: just keep decoding. Further, or backwards: seek. While
        // dragging, "a little" is a few frames: decoding every frame up to a point a second on is slower than a seek.
        var onward = !double.IsNaN(readPos) && t >= readPos - frameLength / 2 && t - readPos < (exact ? 2 : 0.15);
        if (!onward)
        {
            var format = Guid.Empty;
            var pos = new PropVariant { vt = 20, hVal = (long)(t * 1e7) };
            Check(reader.SetCurrentPosition(ref format, ref pos));
            readPos = double.NaN;
        }
        IMFSample? held = null;
        double heldFrom = 0, heldTo = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                Check(reader.ReadSample(FirstVideo, 0, out _, out var flags, out var time, out var sample));
                if ((flags & 0x20) != 0) ReadStride(); // the output format changed
                if (sample == null)
                {                    if ((flags & 0x3) != 0) break; // end of the video, or an error: show the last one read
                    continue;
                }
                var from = time / 1e7;
                var to = from + (sample.GetSampleDuration(out var dur) >= 0 && dur > 0 ? dur / 1e7 : frameLength);
                if (held != null) Marshal.ReleaseComObject(held);
                (held, heldFrom, heldTo) = (sample, from, to);
                readPos = to;
                // Dragging: the first frame after a seek (its keyframe) will do
                if (!exact && !onward) break;
                if (to > t + 1e-6) break;
            }
            if (held == null || ct.IsCancellationRequested) return null;
            var bmp = ToBitmap(held);
            last?.Dispose();
            (last, lastFrom, lastTo) = (new Bitmap(bmp), heldFrom, heldTo);
            return bmp;
        }
        finally
        {
            if (held != null) Marshal.ReleaseComObject(held);
        }
    }

    // One graphics device shared by every reader (the window opens one per video)
    static readonly Lazy<IMFDXGIDeviceManager?> devices = new(CreateDeviceManager);

    static IMFDXGIDeviceManager? CreateDeviceManager()
    {
        try
        {
            // Hardware, with video decoding; BGRA so the frame converter can work on it
            Check(D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x800 | 0x20, IntPtr.Zero, 0, 7, out var device, out _, out var context));
            Marshal.Release(context);
            var dev = Marshal.GetObjectForIUnknown(device);
            Marshal.Release(device);
            // The decoder uses the device from its own threads
            if (dev is ID3D10Multithread mt) mt.SetMultithreadProtected(true);
            Check(MFCreateDXGIDeviceManager(out var token, out var manager));
            Check(manager.ResetDevice(dev, token));
            return manager;
        }
        catch (Exception ex)
        {
            AppLog.Info($"No graphics card decoding for the preview ({ex.Message})");
            return null;
        }
    }

    /// <summary>Asks for decoding on the graphics card; false if there isn't one to use</summary>
    static bool UseGpu(IMFAttributes attrs)
    {
        if (devices.Value is not { } manager) return false;
        var (d3d, hw) = (MF_SOURCE_READER_D3D_MANAGER, MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS);
        return attrs.SetUnknown(ref d3d, manager) >= 0 && attrs.SetUINT32(ref hw, 1) >= 0;
    }

    void SetSize(Size want)
    {
        Check(MFCreateMediaType(out var type));
        try
        {
            var (major, sub, frame) = (MF_MT_MAJOR_TYPE, MF_MT_SUBTYPE, MF_MT_FRAME_SIZE);
            var (video, rgb) = (MFMediaType_Video, MFVideoFormat_RGB32);
            Check(type.SetGUID(ref major, ref video));
            Check(type.SetGUID(ref sub, ref rgb));
            Check(type.SetUINT64(ref frame, ((ulong)want.Width << 32) | (uint)want.Height));
            Check(reader!.SetCurrentMediaType(FirstVideo, IntPtr.Zero, type));
        }
        finally { Marshal.ReleaseComObject(type); }
        size = want;
        ReadStride();
        last?.Dispose();
        last = null;
        // A new size means new frames from here on
        readPos = double.NaN;
    }

    /// <summary>Bytes from one row to the next; negative when the picture is stored bottom row first</summary>
    void ReadStride()
    {
        Check(reader!.GetCurrentMediaType(FirstVideo, out var type));
        try
        {
            var (strideKey, frame) = (MF_MT_DEFAULT_STRIDE, MF_MT_FRAME_SIZE);
            if (type.GetUINT64(ref frame, out var packed) >= 0) size = new Size((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
            stride = type.GetUINT32(ref strideKey, out var s) >= 0 ? (int)s : size.Width * 4;
        }
        finally { Marshal.ReleaseComObject(type); }
    }

    Bitmap ToBitmap(IMFSample sample)
    {
        Check(sample.ConvertToContiguousBuffer(out var buffer));
        try
        {
            Check(buffer.Lock(out var data, out _, out var length));
            try
            {
                var (w, h) = (size.Width, size.Height);
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                var bits = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                var row = new byte[w * 4];
                var step = Math.Abs(stride);
                for (var y = 0; y < h; y++)
                {
                    // Bottom-up storage: the first row in memory is the bottom of the picture
                    var src = stride < 0 ? (h - 1 - y) * step : y * step;
                    if (src + row.Length > length) break;
                    Marshal.Copy(data + src, row, 0, row.Length);
                    Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, row.Length);
                }
                bmp.UnlockBits(bits);
                return bmp;
            }
            finally { buffer.Unlock(); }
        }
        finally { Marshal.ReleaseComObject(buffer); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (reader != null) Marshal.ReleaseComObject(reader);
            reader = null;
            last?.Dispose();
            last = null;
        }
    }

    static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    // ---------- Media Foundation ----------

    const uint FirstVideo = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE;
    static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    static readonly Guid MF_MT_VIDEO_ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public long hVal;
    }

    [DllImport("mfplat.dll")] static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
    [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] static extern int MFCreateDXGIDeviceManager(out uint token, out IMFDXGIDeviceManager manager);
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint levels, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);

    // COM interfaces list every method in order (the slots must line up); only the ones used have real signatures

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void SetString(); void SetBlob();
        [PreserveSig] int SetUnknown(ref Guid key, [MarshalAs(UnmanagedType.IUnknown)] object value);
        void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
    }

    [ComImport, Guid("eb533d5d-2db6-40f8-97a9-494692014f07"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFDXGIDeviceManager
    {
        void CloseDeviceHandle(); void GetVideoService(); void LockDevice(); void OpenDeviceHandle();
        [PreserveSig] int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object device, uint token);
        void TestDevice(); void UnlockDevice();
    }

    [ComImport, Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface ID3D10Multithread
    {
        void Enter(); void Leave();
        [PreserveSig] int SetMultithreadProtected([MarshalAs(UnmanagedType.Bool)] bool protect);
        void GetMultithreadProtected();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64();
        void GetDouble(); void GetGUID(); void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32(); void SetUINT64(); void SetDouble(); void SetGUID();
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetSampleFlags(); void SetSampleFlags(); void GetSampleTime(); void SetSampleTime();
        [PreserveSig] int GetSampleDuration(out long duration);
        void SetSampleDuration(); void GetBufferCount(); void GetBufferByIndex();
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
        void AddBuffer(); void RemoveBufferByIndex(); void RemoveAllBuffers(); void GetTotalLength(); void CopyToBuffer();
    }

    [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        void GetCurrentLength(); void SetCurrentLength(); void GetMaxLength();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        void GetStreamSelection();
        [PreserveSig] int SetStreamSelection(uint stream, [MarshalAs(UnmanagedType.Bool)] bool selected);
        [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(ref Guid timeFormat, ref PropVariant position);
        [PreserveSig] int ReadSample(uint stream, uint control, out uint actualStream, out uint flags, out long timestamp, out IMFSample? sample);
        void Flush(); void GetServiceForStream();
        [PreserveSig] int GetPresentationAttribute(uint stream, ref Guid key, out PropVariant value);
    }
}
