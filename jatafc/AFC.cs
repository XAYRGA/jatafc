using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using System.Text;
using System.Threading.Tasks;
using Be.IO;

namespace jatafc
{

    public enum EncodeFormat
    {
        ADPCM4 = 4,
        PCM16 = 2,
    }


    internal class AFC
    {


        public EncodeFormat format;
        public int LoopStart;
        public int LoopEnd;
        public ushort SampleRate;
        public int SampleCount;
        public bool Loop;

        public short BitsPerSample;
        public short ChannelCount;

        public int BytesPerFrame;
        public int SamplesPerFrame;

        public int TotalBlocks = 0;

        public List<short[]> Channels = new();

        private int[] last = new int[2];
        private int[] penult = new int[2];


        private int sampleOffset = 0;


        private long total_error = 0;
        public static byte[] PCM16ShortToByteBigEndian(short[] pcm)
        {
            var pcmB = new byte[pcm.Length * 2];
            // For some reason this is faster than pinning in memory?
            for (int i = 0; i < pcmB.Length; i += 2)
            {
                var ci = pcm[i / 2];
                pcmB[i] = (byte)(ci >> 8);
                pcmB[i + 1] = (byte)(ci & 0xFF);
            }
            return pcmB;
        }

        private byte[] EncodeADPCM4Block(short[] samples, int sampleCount, ref int last, ref int penult)
        {

            int frameCount = (sampleCount + 16 - 1) / 16; // Roundup samples to 16 or else we'll truncate frames.
            int frameBufferSize = frameCount * 9; 
            byte[] adpcm_data = new byte[frameBufferSize]; 
            int adpcmBufferPosition = 0;

            // transform one frame at a time
            for (int ix = 0; ix < frameCount; ix++)
            {
                short[] wavIn = new short[16];
                byte[] adpcmOut = new byte[9];

                // Extract samples 16 at a time, 1 frame = 16 samples / 9 bytes. 
                for (int k = 0; k < 16 & ( (ix * 16) + k) < samples.Length ; k++)
                    wavIn[k] = samples[(ix * 16) + k];

                var force_coef = -1;
                if (Loop && ((sampleOffset + (ix * 16)) == LoopStart))
                    force_coef = 0;// for some reason at the loop point the coefs have to be zero.  Thank you @ZyphronG
                
                total_error += bananapeel.PCM16TOADPCM4(wavIn, adpcmOut, ref last, ref penult,force_coef); // convert PCM16 -> ADPCM4            

                for (int k = 0; k < 9; k++)
                {
                    adpcm_data[adpcmBufferPosition] = adpcmOut[k]; // dump into ADPCM buffer
                    adpcmBufferPosition++;
                }

            }
            return adpcm_data;
        }


        public void WriteToStream(BeBinaryWriter wrt)
        {

            switch (format)
            {
                case EncodeFormat.ADPCM4:
                    BytesPerFrame = 9;
                    SamplesPerFrame = 16;
                    if (LoopStart % 16 != 0)
                        Console.WriteLine($"WARN: Start loop {LoopStart} is not divisible by 16, corrected to { LoopStart += (16 - (LoopStart % 16))} ");

                    break;
                case EncodeFormat.PCM16:
                    BytesPerFrame = 2;
                    SamplesPerFrame = 1;
                    break;
            }

            // Tell encoder to clamp boundary, nothing plays after the loop.
            if (Loop && (SampleCount > LoopEnd))
                SampleCount = LoopEnd;


            var dataSize = (format == EncodeFormat.ADPCM4 ? ( (SampleCount + 16 -1) / 16) * 18 : SampleCount * 4);

            wrt.Write(dataSize);
            wrt.Write(SampleCount);
            wrt.Write((short)SampleRate);
            wrt.Write((short)format);
            wrt.Write((short)16); // Samples per frame
            wrt.Write((short)30); // Frame rate? 
            wrt.Write(Loop ? 1 : 0);
            wrt.Write(Loop ?  LoopStart : 0 );
            util.padTo(wrt, 32);
            // sample history storage for blocks
            last = new int[2]; // reset
            penult = new int[2]; // reset

            sampleOffset = 0; // reset
  
            WriteInterleavedBlock(wrt);

            Console.WriteLine();
            util.padTo(wrt, 32);
            wrt.Flush();
            wrt.Close();
#if DEBUG 
            Console.WriteLine($"Total sample error {total_error}");
#endif
        }
    
        private int WriteInterleavedBlock(BeBinaryWriter wrt, bool lastBlock = false)
        {

            var SamplesL = sliceSampleArray(Channels[0], 0, SampleCount);
            var SamplesR = sliceSampleArray(Channels[1], 0, SampleCount);


            byte[] writeBuff = new byte[0]; 
            switch (format)
            {
                case EncodeFormat.ADPCM4:
                    {
                        int lastL = 0, penultL = 0;
                        int lastR = 0, penultR = 0;

                        var left = EncodeADPCM4Block(SamplesL, SampleCount, ref lastL, ref penultL);
                        var right = EncodeADPCM4Block(SamplesR, SampleCount, ref penultR, ref lastR);

                        writeBuff = new byte[left.Length + right.Length];

                        // Interleave the samples
                        for (int i = 0 ; i < left.Length / 9; i++) {
                            var offset = i * 18;
                            Array.Copy(left, i * 9, writeBuff, offset, 9);
                            Array.Copy(right, i * 9, writeBuff, offset + 9, 9);
                        }

                        break;
                    }
                case EncodeFormat.PCM16:
                    {
                        var left = PCM16ShortToByteBigEndian(SamplesL);
                        var right = PCM16ShortToByteBigEndian(SamplesR);

                        for (int i = 0; i < left.Length / 2; i+=2)
                        {
                            writeBuff[i * 4] = left[i];
                            writeBuff[i * 4 + 1] = left[i + 1];
                            writeBuff[i * 4 + 2] = right[i];
                            writeBuff[i * 4 + 3] = right[i + 1];
                        }
                    }
                    break;
            }

            wrt.Write(writeBuff);
           
            return 0;
        }

        private short[] sliceSampleArray(short[] samples, int start, int sampleCount)
        {

            var ret = new short[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                if ( (i +start) < samples.Length )
                    ret[i] = samples[start + (i)];
                else
                    ret[i] = 0;
            return ret;
        }

    }
}
