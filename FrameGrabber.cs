using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace Base1
{
    /// <summary>Encapsulates frame grabbing functions of a Euresys PICOLO PCIe board.</summary>
    internal sealed class FrameGrabber : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct SignalInfo
        {
            public IntPtr Context;
            public uint Instance;
            public int Signal;
            public uint Handle;
            public uint SignalContext;
        };

        // Constants and statics
        private const int AcquisitionFailure = 7;
        private const uint Configuration = 0x20000000;
        private const int EndChannelActivity = 12;
        private const uint ErrorDescription = 98 << 14;
        private const int Indeterminate = -1;
        private const int MaxValueLength = 1024;
        private const int SignalAny = 0;
        private const uint SignalEnable = 24 << 14;
        private const uint SignalHandling = 74 << 14;
        private const uint SurfaceState = 31 << 14;
        private const int SurfaceStateFree = 1;
        private const int SurfaceFilled = 2;
        private const int WaitingSignaling = 3;
        private static int instanceCount = 0;

        // Events
        public event EventHandler<FrameEventArgs> OnFrame;

        // Instance fields
        private readonly uint channel;
        private Thread captureThread;
        private bool started;

        #region Native Methods
        [DllImport("MultiCam.dll", EntryPoint = "McOpenDriver")]
        private static extern int OpenDriver(IntPtr instanceName);

        [DllImport("MultiCam.dll", EntryPoint = "McCloseDriver")]
        private static extern int CloseDriver();

        [DllImport("MultiCam.dll", EntryPoint = "McCreate")]
        private static extern int Create(uint modelInstance, out uint instance);

        [DllImport("MultiCam.dll", EntryPoint = "McCreateNm")]
        private static extern int Create(string modelName, out uint instance);

        [DllImport("MultiCam.dll", EntryPoint = "McGetParamNmInt")]
        private static extern int GetParameter(uint instance, string parameterName, out int value);

        [DllImport("MultiCam.dll", EntryPoint = "McGetParamNmPtr")]
        private static extern int GetParameter(uint instance, string parameterName, out IntPtr value);

        [DllImport("MultiCam.dll", EntryPoint = "McGetParamStr")]
        private static extern int GetStringParameter(uint instance, uint parameterId, IntPtr value, uint maxLength);

        [DllImport("MultiCam.dll", EntryPoint = "McSetParamInt")]
        private static extern int SetParameter(uint instance, uint parameterId, int value);

        [DllImport("MultiCam.dll", EntryPoint = "McSetParamNmInt")]
        private static extern int SetParameter(uint instance, string parameterName, int value);

        [DllImport("MultiCam.dll", EntryPoint = "McSetParamNmStr")]
        private static extern int SetParameter(uint instance, string parameterName, string value);

        [DllImport("MultiCam.dll", EntryPoint = "McSetParamStr")]
        private static extern int SetParameter(uint instance, uint parameterId, string value);

        [DllImport("MultiCam.dll", EntryPoint = "McWaitSignal")]
        private static extern int WaitSignal(uint instance, int signal, uint timeout, out SignalInfo info);
        #endregion

        public FrameGrabber(int driverIndex, string connector, string videoStandard)
        {
            // Open the MultiCam driver when the first frame grabber is instantiated
            if (Interlocked.Increment(ref instanceCount) == 1)
                Assert(OpenDriver(IntPtr.Zero));

            // Create a channel and associate it with the required settings
            Assert(Create("CHANNEL", out channel));
            Assert(SetParameter(channel, "DriverIndex", driverIndex));
            Assert(SetParameter(channel, "Connector", connector));
            Assert(SetParameter(channel, "Standard", videoStandard));
            Assert(SetParameter(channel, "ColorFormat", "RGB24"));
            Assert(SetParameter(channel, "AcquisitionMode", "VIDEO"));
            Assert(SetParameter(channel, "TrigMode", "IMMEDIATE"));
            Assert(SetParameter(channel, "NextTrigMode", "REPEAT"));
            Assert(SetParameter(channel, "SeqLength_Fr", Indeterminate));

            // Enable MultiCam signals
            Assert(SetParameter(channel, SignalEnable + SurfaceFilled, "ON"));
            Assert(SetParameter(channel, SignalEnable + AcquisitionFailure, "ON"));
            Assert(SetParameter(channel, SignalEnable + EndChannelActivity, "ON"));

            // Set Multicam signal to use wait-signaling
            Assert(SetParameter(channel, SignalHandling + SurfaceFilled, WaitingSignaling));
            Assert(SetParameter(channel, SignalHandling + AcquisitionFailure, WaitingSignaling));
            Assert(SetParameter(channel, SignalHandling + EndChannelActivity, WaitingSignaling));

            // Prepare the channel in order to minimize the acquisition sequence startup latency
            Assert(SetParameter(channel, "ChannelState", "READY"));
        }

        #region Methods
        [Conditional("DEBUG"), DebuggerHidden]
        private static void Assert(int result)
        {
            if (result == 0)
                return;

            string message;
            var messagePtr = IntPtr.Zero;
            var parameterId = ErrorDescription + (uint)Math.Abs(result);

            try
            {
                messagePtr = Marshal.AllocHGlobal(MaxValueLength + 1);

                if (GetStringParameter(Configuration, parameterId, messagePtr, MaxValueLength) != 0)
                    message = Marshal.PtrToStringAnsi(messagePtr);

                throw new InvalidTimeZoneException();
            }
            catch
            {
                message = "Unknown error";
            }
            finally
            {
                if (messagePtr != IntPtr.Zero)
                    Marshal.FreeHGlobal(messagePtr);
            }

            throw new Exception(message);
        }

        private void CaptureThread()
        {
            Assert(SetParameter(channel, "ChannelState", "ACTIVE"));

            try
            {
                while (this.started)
                {
                    // Wait up to 1s for a signal
                    SignalInfo info;
                    Assert(WaitSignal(channel, SignalAny, 1000, out info));

                    switch (info.Signal)
                    {
                        case SurfaceFilled:
                            // We've got a good signal
                            FireOnFrame(info);
                            Assert(SetParameter(info.Handle, SurfaceState, SurfaceStateFree));
                            break;

                        case AcquisitionFailure:
                            // Uh oh...
                            break;

                        case EndChannelActivity:
                            // TODO: Throw another event in this case?
                            this.started = false;
                            break;

                        default:
                            // Unknown signal
                            break;
                    }
                }
            }
            finally
            {
                Assert(SetParameter(channel, "ChannelState", "IDLE"));
                this.started = false;
            }
        }

        private void FireOnFrame(SignalInfo info)
        {
            // Get image data
            int width, height, stride;
            Assert(GetParameter(channel, "ImageSizeX", out width));
            Assert(GetParameter(channel, "ImageSizeY", out height));
            Assert(GetParameter(channel, "BufferPitch", out stride));

            // Get image address
            IntPtr buffer;
            Assert(GetParameter(info.Handle, "SurfaceAddr", out buffer));

            // Send to our listeners
            var handler = this.OnFrame;
            if (handler != null)
                handler(this, new FrameEventArgs
                {
                    Buffer = buffer,
                    Width = width,
                    Height = height,
                    Stride = stride,
                });
        }

        public void Start()
        {
            this.started = true;

            this.captureThread = new Thread(CaptureThread);
            this.captureThread.IsBackground = true;
            this.captureThread.Start();
        }

        public void Stop()
        {
            // Flipping this flag will cause the capture thread to starve
            this.started = false;
        }
        #endregion

        #region IDisposable Implementation
        public void Dispose()
        {
            this.Stop();

            // Close the MultiCam driver when the last frame grabber is disposed
            if (Interlocked.Decrement(ref instanceCount) == 0)
                Assert(CloseDriver());
        }
        #endregion
    }
}
