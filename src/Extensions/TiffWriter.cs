using Bonsai;
using System;
using System.ComponentModel;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using OpenCV.Net;
using BitMiracle.LibTiff.Classic;

[Combinator]
[Description("Writes a sequence of IplImage frames to a multi-page TIFF stack file.")]
[WorkflowElementCategory(ElementCategory.Sink)]
public class TiffWriter
{
    private string fileName = string.Empty;
    private bool useBigTiff = false;
    private Compression compression = Compression.NONE;
    private bool overwrite = false;

    [Description("The path to the output file.")]
    public string FileName
    {
        get { return fileName; }
        set { fileName = value; }
    }

    [Description("Specifies whether to use BigTIFF format.")]
    public bool UseBigTiff
    {
        get { return useBigTiff; }
        set { useBigTiff = value; }
    }

    [Description("The compression algorithm to use.")]
    public Compression Compression
    {
        get { return compression; }
        set { compression = value; }
    }

    [Description("Specifies whether to overwrite the file if it already exists.")]
    public bool Overwrite
    {
        get { return overwrite; }
        set { overwrite = value; }
    }

    public IObservable<IplImage> Process(IObservable<IplImage> source)
    {
        return Observable.Using(
            () => new TiffStackWriter(fileName, useBigTiff, compression, overwrite),
            writer =>
            {
                return source.Do(image => writer.WriteFrame(image));
            });
    }

    private class TiffStackWriter : IDisposable
    {
        private readonly Tiff tiffStack;
        private int currentFrameIdx;
        private byte[] rowBuffer; // reusable buffer
        private byte[] stripBuffer; // reusable buffer

        private Compression compression;

        public TiffStackWriter(string path, bool useBigTiff, Compression compression, bool overwrite)
        {
            if (!overwrite && System.IO.File.Exists(path))
            {
                throw new InvalidOperationException(string.Format("File already exists: {0}", path));
            }
            this.compression = compression;
            var mode = useBigTiff ? "w8" : "w";
            tiffStack = Tiff.Open(path, mode);
            if (tiffStack == null)
            {
                throw new InvalidOperationException(string.Format("Failed to create TIFF file: {0}", path));
            }

            currentFrameIdx = 0;
            rowBuffer = null;
        }

        public void WriteFrame(IplImage image)
        {
            var width = image.Width;
            var height = image.Height;
            var channels = image.Channels;
            var depth = image.Depth;

            int bitsPerSample;
            SampleFormat sampleFormat;
            GetTiffSampleInfo(depth, out bitsPerSample, out sampleFormat);

            tiffStack.SetField(TiffTag.IMAGEWIDTH, width);
            tiffStack.SetField(TiffTag.IMAGELENGTH, height);
            tiffStack.SetField(TiffTag.SAMPLESPERPIXEL, channels);
            tiffStack.SetField(TiffTag.BITSPERSAMPLE, bitsPerSample);
            tiffStack.SetField(TiffTag.SAMPLEFORMAT, sampleFormat);
            tiffStack.SetField(TiffTag.ORIENTATION, Orientation.TOPLEFT);
            tiffStack.SetField(TiffTag.PLANARCONFIG, PlanarConfig.CONTIG);
            tiffStack.SetField(TiffTag.COMPRESSION, this.compression);
            tiffStack.SetField(TiffTag.ROWSPERSTRIP, height);

            if (channels == 1)
            {
                tiffStack.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            }
            else if (channels == 3)
            {
                tiffStack.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
            }
            else if (channels == 4)
            {
                tiffStack.SetField(TiffTag.PHOTOMETRIC, Photometric.RGB);
                tiffStack.SetField(TiffTag.EXTRASAMPLES, 1, new short[] { (short)ExtraSample.UNASSALPHA });
            }
            else
            {
                tiffStack.SetField(TiffTag.PHOTOMETRIC, Photometric.MINISBLACK);
            }

            tiffStack.SetField(TiffTag.SUBFILETYPE, FileType.PAGE);
            tiffStack.SetField(TiffTag.PAGENUMBER, currentFrameIdx, 0);

            var bytesPerPixel = (bitsPerSample / 8) * channels;
            var rowBytes = width * bytesPerPixel;
            var widthStep = image.WidthStep;

            if (rowBuffer == null || rowBuffer.Length < rowBytes)
            {
                rowBuffer = new byte[rowBytes];
            }

            var imageData = image.ImageData;

            if (widthStep == rowBytes)
            {
                var totalBytes = height * rowBytes;
                if (stripBuffer == null || stripBuffer.Length < totalBytes)
                {
                    stripBuffer = new byte[totalBytes];
                }
                Marshal.Copy(imageData, stripBuffer, 0, totalBytes);
                tiffStack.WriteEncodedStrip(0, stripBuffer, totalBytes);
            }
            else
            {
                for (int row = 0; row < height; row++)
                {
                    var rowPtr = IntPtr.Add(imageData, row * widthStep);
                    Marshal.Copy(rowPtr, rowBuffer, 0, rowBytes);
                    tiffStack.WriteScanline(rowBuffer, row);
                }
            }

            tiffStack.WriteDirectory();
            currentFrameIdx++;
        }

        public void Dispose()
        {
            if (tiffStack != null)
            {
                tiffStack.Dispose();
            }
        }

        private static void GetTiffSampleInfo(IplDepth depth, out int bitsPerSample, out SampleFormat sampleFormat)
        {
            switch (depth)
            {
                case IplDepth.U8:
                    bitsPerSample = 8;
                    sampleFormat = SampleFormat.UINT;
                    break;
                case IplDepth.S8:
                    bitsPerSample = 8;
                    sampleFormat = SampleFormat.INT;
                    break;
                case IplDepth.U16:
                    bitsPerSample = 16;
                    sampleFormat = SampleFormat.UINT;
                    break;
                case IplDepth.S16:
                    bitsPerSample = 16;
                    sampleFormat = SampleFormat.INT;
                    break;
                case IplDepth.S32:
                    bitsPerSample = 32;
                    sampleFormat = SampleFormat.INT;
                    break;
                case IplDepth.F32:
                    bitsPerSample = 32;
                    sampleFormat = SampleFormat.IEEEFP;
                    break;
                case IplDepth.F64:
                    bitsPerSample = 64;
                    sampleFormat = SampleFormat.IEEEFP;
                    break;
                default:
                    throw new NotSupportedException(string.Format("Unsupported image depth: {0}", depth));
            }
        }
    }
}
