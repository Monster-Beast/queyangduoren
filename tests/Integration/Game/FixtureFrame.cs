using System;
using System.IO;
using UnityEngine;

namespace Queyang.Integration
{
    internal sealed class FixtureFrame
    {
        readonly string path;
        float deadline;
        internal bool Requested { get; private set; }
        internal FixtureFrame(string path) { this.path = path; }

        internal void Capture()
        {
            if (Requested || File.Exists(path)) throw new IOException("Unexpected existing capture: " + path);
            if (Screen.width < 640 || Screen.height < 360) throw new IOException("No graphical gameplay viewport");
            Requested = true;
            deadline = Time.realtimeSinceStartup + 20f;
            ScreenCapture.CaptureScreenshot(path);
        }

        internal bool Complete
        {
            get
            {
                if (!Requested) return false;
                if (Time.realtimeSinceStartup > deadline) throw new TimeoutException("Incomplete gameplay screenshot: " + path);
                try
                {
                    using (var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var reader = new BinaryReader(file))
                    {
                        if (file.Length < 45) return false;
                        // Check the PNG signature and the complete final IEND chunk (including its CRC).
                        if (reader.ReadUInt64() != 0x0A1A0A0D474E5089UL) throw new InvalidDataException("Invalid gameplay screenshot: " + path);
                        file.Seek(-12, SeekOrigin.End);
                        return reader.ReadUInt32() == 0 && reader.ReadUInt64() == 0x826042AE444E4549UL;
                    }
                }
                catch (IOException) { return false; } // Unity may still own the file or not have opened it.
            }
        }
    }
}
