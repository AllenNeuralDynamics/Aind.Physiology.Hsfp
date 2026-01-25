using Bonsai;
using Bonsai.Vision.Design;
using AllenNeuralDynamics.HamamatsuCamera.Models;
using System.Collections.Generic;
using System.Linq;

[assembly: TypeVisualizer(typeof(FrameVisualizer), Target = typeof(Frame))]

public class FrameVisualizer : IplImageVisualizer
{
    public override void Show(object value)
    {
        Frame frame = value as Frame;
        if (frame != null)
        {
            if (frame.Image != null)
            {
                base.Show(value);
            }
        }

    }

    protected override void ShowMashup(IList<object> values)
    {
        IList<Frame> frames = values as IList<Frame>;
        if (frames != null)
        {
            IList<object> images = frames.Select(f => (object)f.Image).Where(img => img != null).ToList();
            base.ShowMashup(images);
        }
    }
}