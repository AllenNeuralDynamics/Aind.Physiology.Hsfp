using Bonsai;
using System;
using System.ComponentModel;
using System.Linq;
using System.Reactive.Linq;
using OpenCV.Net;
using Bonsai.Vision;

[Combinator]
[Description("Allows selecting a set of rectangles from an image.")]
[WorkflowElementCategory(ElementCategory.Combinator)]
[DefaultProperty("Rectangles")]
public class SelectRectangles
{

    private string Label = "Channel";
    public string label
    {
        get { return Label; }
        set
        {
            if (!string.IsNullOrEmpty(value))
            {
                Label = value;
            }
        }
    }

    private Rect[] rectangles = new Rect[0];
    public Rect[] Rectangles {
        get { return rectangles; }
        set
        {
            if (value != null && value.Length > 0)
            {
                rectangles = value;
            }
        }
        }

    public event Action RefreshRequested;

    private bool refresh;
    [Description("Triggers a refresh of the visualizer regions.")]
    public bool Refresh
    {
        get {return refresh;}
        set
        {
            if (value)
            {
                refresh = false; // Reset the property after triggering
                var handler = RefreshRequested;
                if (handler != null) handler.Invoke();
            }
        }
    }

    public IObservable<CroppedRegions> Process(IObservable<IplImage> source)
    {
        return source.Select(value=> new CroppedRegions(){
            Label = this.Label,
            Image = value,
            Regions = this.Rectangles
        });
    }
}
