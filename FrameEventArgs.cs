using System;

namespace Base1
{
    internal sealed class FrameEventArgs : EventArgs
    {
        public IntPtr Buffer { get; set; }
        public int Height { get; set; }
        public int Stride { get; set; }
        public int Width { get; set; }
    }
}
