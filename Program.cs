namespace Base1
{
    class Program
    {
        static void Main(string[] args)
        {
            using (var leftFrameGrabber = new FrameGrabber(0, "VID1", "NTSC"))
            {
                leftFrameGrabber.OnFrame += leftFrameGrabber_OnFrame;
                leftFrameGrabber.Start();
            }
        }

        static void leftFrameGrabber_OnFrame(object sender, FrameEventArgs e)        
        {
        }
    }
}
