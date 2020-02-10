using System;
using System.Collections;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using GHIElectronics.TinyCLR.Native;

namespace GHIElectronics.TinyCLR.Drivers.Media {
    public sealed class Mjpeg : IDisposable{
        const int BLOCK_SIZE = 4 * 1024;

        public delegate void DataDecodedEventHandler(byte[] data);

        //public event DataDecodedEventHandler Mp3DataDecodedEvent;

        private FileStream streamToBuffer;
        private byte[] dataToBuffer;

        private bool isDecoding;

        private Graphics screen;

        public class Setting {
            public int BufferSize { get; set; } = 2 * 1024 * 1024;
            public int BufferCount { get; set; } = 3;
            public Graphics Screen { get; set; }
        }

        private class Fifo {
            internal int bufferSize;
            internal int bufferCount;

            internal int fifoIn;
            internal int fifoOut;
            internal int fifoCount;

            internal byte[][] buffer;
            internal IntPtr[] unmanagedPtr;
        }

        private Fifo fifo;

        private class HeaderInfo {
            public int TimeBetweenFrames { get; internal set; }
            public int Width { get; internal set; }
            public int Height { get; internal set; }
            public int SuggestedBufferSize { get; internal set; }
            public int TotalFrames { get; internal set; }

        }

        private HeaderInfo headerInfo;
        public Mjpeg(Setting setting) {
            this.fifo = new Fifo {
                bufferSize = setting.BufferSize,
                bufferCount = setting.BufferCount
            };

            this.fifo.buffer = new byte[this.fifo.bufferCount][];
            this.fifo.unmanagedPtr = new IntPtr[this.fifo.bufferCount];

            for (var c = 0; c < this.fifo.bufferCount; c++) {

                if (Memory.UnmanagedMemory.FreeBytes > this.fifo.bufferSize) {
                    var ptr = Memory.UnmanagedMemory.Allocate(this.fifo.bufferSize);
                    this.fifo.buffer[c] = Memory.UnmanagedMemory.ToBytes(ptr, this.fifo.bufferSize);

                    this.fifo.unmanagedPtr[c] = ptr;
                }
                else {
                    this.fifo.buffer[c] = new byte[this.fifo.bufferSize];
                }
            }

            this.screen = setting.Screen;
            this.headerInfo = new HeaderInfo();

            if (this.screen == null)
                throw new NullReferenceException("");
        }

        public void StartDecode(FileStream stream) {
            this.streamToBuffer = stream;

            this.isDecoding = true;

            var pushThread = new Thread(this.PushStreamToBuffer);

            pushThread.Start();

            var pop2Device = new Thread(this.Pop2Device);

            pop2Device.Start();
        }

        public void StartDecode(byte[] data) {
            this.dataToBuffer = data;

            this.isDecoding = true;

            var pushThread = new Thread(this.PushDataToBuffer);

            pushThread.Start();

            var pop2Device = new Thread(this.Pop2Device);

            pop2Device.Start();
        }

        public void StopDecode() => this.isDecoding = false;

        private void PushStreamToBuffer() {
            var streamLength = this.streamToBuffer.Length;
            var i = 0;

            while (i < streamLength) {
                Thread.Sleep(1);

                if (!this.isDecoding)
                    break;

                if (this.fifo.fifoCount == this.fifo.bufferCount) {
                    continue;
                }

                var lengthToBuffer = (int)((this.fifo.bufferSize < streamLength - i) ? this.fifo.bufferSize : (streamLength - i));

                var block = lengthToBuffer / BLOCK_SIZE;
                var remain = lengthToBuffer % BLOCK_SIZE;

                var index = 0;
                var id = this.fifo.fifoIn;

                while (block > 0) {
                    this.streamToBuffer.Read(this.fifo.buffer[id], index, BLOCK_SIZE);
                    index += BLOCK_SIZE;
                    block--;

                    i += BLOCK_SIZE;

                    Thread.Sleep(1);
                }

                if (remain > 0) {
                    this.streamToBuffer.Read(this.fifo.buffer[id], index, remain);

                    i += remain;
                }

                lock (this.fifo) {
                    this.fifo.fifoCount++;
                }

                this.fifo.fifoIn++;

                if (this.fifo.fifoIn == this.fifo.bufferCount) {
                    this.fifo.fifoIn = 0;
                }
            }
        }
        private void PushDataToBuffer() {
            var dataLength = this.dataToBuffer.Length;
            var i = 0;

            while (i < dataLength) {

                Thread.Sleep(1);

                if (!this.isDecoding)
                    break;

                if (this.fifo.fifoCount == this.fifo.bufferCount) {
                    continue;
                }

                var lengthToBuffer = (int)((this.fifo.bufferSize < dataLength - i) ? this.fifo.bufferSize : (dataLength - i));

                var block = lengthToBuffer / BLOCK_SIZE;
                var remain = lengthToBuffer % BLOCK_SIZE;

                var index = 0;
                var id = this.fifo.fifoIn;

                while (block > 0) {
                    Array.Copy(this.dataToBuffer, i, this.fifo.buffer[id], index, BLOCK_SIZE);

                    index += BLOCK_SIZE;
                    i += BLOCK_SIZE;

                    block--;
                }

                if (remain > 0) {
                    Array.Copy(this.dataToBuffer, i, this.fifo.buffer[id], index, remain);

                    i += remain;
                }

                lock (this.fifo) {
                    this.fifo.fifoCount++;
                }

                this.fifo.fifoIn++;

                if (this.fifo.fifoIn == this.fifo.bufferCount) {
                    this.fifo.fifoIn = 0;
                }
            }
        }

        private void Pop2Device() {

            var decodeHeader = true;
            while (this.isDecoding) {
                Thread.Sleep(1);

                lock (this.fifo) {
                    if (this.fifo.fifoCount == 0) {
                        continue;
                    }
                }

                var id = this.fifo.fifoOut;

                for (var i = 0; i < this.fifo.bufferSize - 4; i++) {
                    // Decode header
                    var t1 = System.DateTime.Now.Ticks;
                    var foundData = false;

                    if (decodeHeader) {

                        var riff = System.Text.Encoding.UTF8.GetString(this.fifo.buffer[id], 0, 4);
                        var type = System.Text.Encoding.UTF8.GetString(this.fifo.buffer[id], 8, 4);

                        i = 0x20; // fps

                        this.headerInfo.TimeBetweenFrames = (this.fifo.buffer[id][i] | (this.fifo.buffer[id][i + 1] << 8) | (this.fifo.buffer[id][i + 2] << 16) | (this.fifo.buffer[id][i + 3] << 24)) / 1000;

                        i += 4 * 4;

                        this.headerInfo.TotalFrames = this.fifo.buffer[id][i] | (this.fifo.buffer[id][i + 1] << 8) | (this.fifo.buffer[id][i + 2] << 16) | (this.fifo.buffer[id][i + 3] << 24);

                        i += 3 * 4;

                        this.headerInfo.SuggestedBufferSize = this.fifo.buffer[id][i] | (this.fifo.buffer[id][i + 1] << 8) | (this.fifo.buffer[id][i + 2] << 16) | (this.fifo.buffer[id][i + 3] << 24);

                        i += 4;

                        this.headerInfo.Width = this.fifo.buffer[id][i] | (this.fifo.buffer[id][i + 1] << 8) | (this.fifo.buffer[id][i + 2] << 16) | (this.fifo.buffer[id][i + 3] << 24);

                        i += 4;

                        this.headerInfo.Height = this.fifo.buffer[id][i] | (this.fifo.buffer[id][i + 1] << 8) | (this.fifo.buffer[id][i + 2] << 16) | (this.fifo.buffer[id][i + 3] << 24);

                        i += 4;

                        decodeHeader = false;
                    }

                    // Decode image
                    {
                        var jpegLength = 0;

                        if (this.fifo.buffer[id][i] == (byte)'0'
                        && this.fifo.buffer[id][i + 1] == (byte)'0'
                        && this.fifo.buffer[id][i + 2] == (byte)'d'
                        && this.fifo.buffer[id][i + 3] == (byte)'c') {
                            i += 4;

                            jpegLength = (this.fifo.buffer[id][i] | (this.fifo.buffer[id][i + 1] << 8) | (this.fifo.buffer[id][i + 2] << 16) | (this.fifo.buffer[id][i + 3] << 24));

                            i += 4;

                            if (i + jpegLength > this.fifo.buffer[id].Length)
                                break;

                            if (jpegLength > 0) {

                                using (var image = new Bitmap(this.fifo.buffer[id], i, jpegLength, BitmapImageType.Jpeg)) {

                                    this.screen.DrawImage(image, 0, 0, image.Width, image.Height);

                                    this.screen.Flush();
                                }

                                GC.WaitForPendingFinalizers();

                                i += (jpegLength - 1);

                                foundData = true;

                            }
                        }
                    }
                    

                    if (foundData) {
                        var t2 = (int)(System.DateTime.Now.Ticks - t1) / 10000;

                        if (t2 < this.headerInfo.TimeBetweenFrames)
                            Thread.Sleep(this.headerInfo.TimeBetweenFrames - t2);
                    }
                }

                lock (this.fifo) {
                    this.fifo.fifoCount--;
                }

                this.fifo.fifoOut++;

                if (this.fifo.fifoOut == this.fifo.bufferCount)
                    this.fifo.fifoOut = 0;
            }
        }

        public void Dispose() {
            this.isDecoding = false;

            if (this.fifo.unmanagedPtr != null) {
                for (var c = 0; c < this.fifo.bufferCount; c++) {
                    Memory.UnmanagedMemory.Free(this.fifo.unmanagedPtr[c]);
                }
            }
        }
    }
}
