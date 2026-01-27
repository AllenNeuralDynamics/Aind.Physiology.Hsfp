using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;
using Bonsai.Vision;

[Combinator]
[Description("Computes the luminance of cropped regions from the input image and corresponding regions.")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ProcessCroppedRegions
{
    public IObservable<double[]> Process(IObservable<CroppedRegions> source)
    {
        return source.Select(value =>
        {
            if (value == null || value.Regions.Count() == 0)
            {
                return new double[0];
            }
            else
            {
                double[] intensities = new double[value.Regions.Count()];
                for (int i = 0; i < value.Regions.Count(); i++)
                {
                    intensities[i] = ComputeIntensity(value.Image, value.Regions[i]);
                }
                return intensities;
            }
        });
    }

    static double ComputeIntensity(IplImage image, Rect region)
    {
        if (image == null || region.Width <= 0 || region.Height <= 0)
        {
            return 0;
        }

        using (var cropped = image.GetSubRect(region))
        {
            var mean = CV.Avg(cropped);
            return mean.Val0;
        }
    }
}
