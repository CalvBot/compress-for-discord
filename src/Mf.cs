using System.Runtime.InteropServices;

namespace CompressForDiscord;

/// <summary>Windows' Media Foundation, as much of it as the frame and audio readers use</summary>
static class Mf
{
    internal static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

    /// <summary>Media Foundation is started (once, for the whole app)</summary>
    internal static readonly Lazy<bool> Started = new(() => MFStartup(0x00020070, 0) >= 0);

    // ---------- Media Foundation ----------

    internal const uint FirstVideo = 0xFFFFFFFC, AllStreams = 0xFFFFFFFE, AnyStream = 0xFFFFFFFE;
    internal static readonly Guid MFMediaType_Audio = new("73647561-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFAudioFormat_PCM = new("00000001-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MF_MT_AUDIO_BITS_PER_SAMPLE = new("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
    internal static readonly Guid MF_PD_DURATION = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    internal const uint MediaSource = 0xFFFFFFFF;
    internal static readonly Guid MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING = new("0f81da2c-b537-4672-a8b2-a681b17307a3");
    internal static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    internal static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    internal static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    internal static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    internal static readonly Guid MF_MT_VIDEO_ROTATION = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    internal static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    internal static readonly Guid MF_SOURCE_READER_D3D_MANAGER = new("ec822da2-e1e9-4b29-a0d8-563c719f5269");
    internal static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    internal static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    internal static readonly Guid MFVideoFormat_RGB32 = new("00000016-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    internal struct PropVariant
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public long hVal;
    }

    [DllImport("mfplat.dll")] internal static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] internal static extern int MFCreateAttributes(out IMFAttributes attributes, uint initialSize);
    [DllImport("mfplat.dll")] internal static extern int MFCreateMediaType(out IMFMediaType type);
    [DllImport("mfplat.dll")] internal static extern int MFCreateDXGIDeviceManager(out uint token, out IMFDXGIDeviceManager manager);
    [DllImport("d3d11.dll")]
    internal static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint levels, uint sdkVersion,
        out IntPtr device, out int featureLevel, out IntPtr context);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    internal static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);

    // COM interfaces list every method in order (the slots must line up); only the ones used have real signatures

    [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFAttributes
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
    internal interface IMFDXGIDeviceManager
    {
        void CloseDeviceHandle(); void GetVideoService(); void LockDevice(); void OpenDeviceHandle();
        [PreserveSig] int ResetDevice([MarshalAs(UnmanagedType.IUnknown)] object device, uint token);
        void TestDevice(); void UnlockDevice();
    }

    [ComImport, Guid("9b7e4e00-342c-4106-a19f-4f2704f689f0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ID3D10Multithread
    {
        void Enter(); void Leave();
        [PreserveSig] int SetMultithreadProtected([MarshalAs(UnmanagedType.Bool)] bool protect);
        void GetMultithreadProtected();
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFMediaType
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare();
        [PreserveSig] int GetUINT32(ref Guid key, out uint value);
        [PreserveSig] int GetUINT64(ref Guid key, out ulong value);
        void GetDouble();
        [PreserveSig] int GetGUID(ref Guid key, out Guid value);
        void GetStringLength(); void GetString(); void GetAllocatedString();
        void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown(); void SetItem(); void DeleteItem(); void DeleteAllItems();
        [PreserveSig] int SetUINT32(ref Guid key, uint value);
        [PreserveSig] int SetUINT64(ref Guid key, ulong value);
        void SetDouble();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
        void SetString(); void SetBlob(); void SetUnknown(); void LockStore(); void UnlockStore(); void GetCount(); void GetItemByIndex(); void CopyAllItems();
        void GetMajorType(); void IsCompressedFormat(); void IsEqual(); void GetRepresentation(); void FreeRepresentation();
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSample
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
    internal interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
        [PreserveSig] int Unlock();
        void GetCurrentLength(); void SetCurrentLength(); void GetMaxLength();
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IMFSourceReader
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
