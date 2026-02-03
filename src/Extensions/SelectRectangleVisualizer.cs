using System;
using Bonsai.Vision.Design;
using Bonsai;
using Bonsai.Dag;
using OpenCV.Net;
using Bonsai.Vision;
using System.Reactive.Linq;
using Bonsai.Design;
using Bonsai.Expressions;
using System.Linq;
using System.Windows.Forms;
using System.Collections.Generic;

[assembly: TypeVisualizer(typeof(SelectRectangleVisualizer), Target = typeof(SelectRectangles))]

public class SelectRectangleVisualizer : DialogTypeVisualizer
{
    PublicImageRectanglePicker rectanglePicker;
    IDisposable inputHandle;

    /// <inheritdoc/>
    public override void Show(object value)
    {
    }

    /// <inheritdoc/>
    public override void Load(IServiceProvider provider)
    {
        var context = (ITypeVisualizerContext)provider.GetService(typeof(ITypeVisualizerContext));
        var visualizerElement = ExpressionBuilder.GetVisualizerElement(context.Source);
        var selectRegions = (SelectRectangles)ExpressionBuilder.GetWorkflowElement(visualizerElement.Builder);

        rectanglePicker = new PublicImageRectanglePicker {  LabelRegions = true, Dock = DockStyle.Fill };
        UpdateRegions(selectRegions);

        selectRegions.RefreshRequested += () => UpdateRegions(selectRegions);

        rectanglePicker.RegionsChanged += delegate
        {
            selectRegions.Rectangles = rectanglePicker.Regions.ToArray()
                .Select(region => new Rect(region.X, region.Y, region.Width, region.Height))
                .ToArray();
        };

        var imageInput = VisualizerHelper.ImageInput(provider);
        if (imageInput != null)
        {
            inputHandle = imageInput.Subscribe(value => rectanglePicker.Image = (IplImage)value);
            rectanglePicker.HandleDestroyed += delegate { inputHandle.Dispose(); };
        }

        var visualizerService = (IDialogTypeVisualizerService)provider.GetService(typeof(IDialogTypeVisualizerService));
        if (visualizerService != null)
        {
            visualizerService.AddControl(rectanglePicker);
        }
    }

    private void UpdateRegions(SelectRectangles selectRegions)
    {
        if (selectRegions == null || rectanglePicker == null) return;
        rectanglePicker.Regions.Clear();
        foreach (var rect in selectRegions.Rectangles)
        {
            var region = new Rect(rect.X, rect.Y, rect.Width, rect.Height);
            rectanglePicker.Regions.Add(region);
        }
    }

    /// <inheritdoc/>
    public override void Unload()
    {
        if (rectanglePicker != null)
        {
            rectanglePicker.Dispose();
            rectanglePicker = null;
        }
    }
}

static class VisualizerHelper
    {

        internal static IObservable<object> ImageInput(IServiceProvider provider)
        {
            InspectBuilder inspectBuilder = null;
            WorkflowBuilder workflowBuilder = (WorkflowBuilder)provider.GetService(typeof(WorkflowBuilder));

            var context = (ITypeVisualizerContext)provider.GetService(typeof(ITypeVisualizerContext));
            var visualizerElement = ExpressionBuilder.GetVisualizerElement(context.Source);

            if (workflowBuilder != null && context != null)
            {
                inspectBuilder = workflowBuilder.Workflow.DescendantNodes().FirstOrDefault(
                    n => n.Successors.Any(s => s.Target.Value == visualizerElement)).Value as InspectBuilder;
                        if (inspectBuilder != null && inspectBuilder.ObservableType == typeof(IplImage))
                {
                    return inspectBuilder.Output.Merge();
                }
            }



            return null;
        }


        public static IEnumerable<Node<ExpressionBuilder, ExpressionBuilderArgument>> DescendantNodes(this ExpressionBuilderGraph source)
        {
            var stack = new Stack<IEnumerator<Node<ExpressionBuilder, ExpressionBuilderArgument>>>();
            stack.Push(source.GetEnumerator());
 
            while (stack.Count > 0)
            {
                var nodeEnumerator = stack.Peek();
                while (true)
                {
                    if (!nodeEnumerator.MoveNext())
                    {
                        stack.Pop();
                        break;
                    }
 
                    var node = nodeEnumerator.Current;
                    var builder = ExpressionBuilder.Unwrap(node.Value);
                    yield return node;
 
                    var workflowBuilder = builder as IWorkflowExpressionBuilder;
                    if (workflowBuilder != null && workflowBuilder.Workflow != null)
                    {
                        stack.Push(workflowBuilder.Workflow.GetEnumerator());
                        break;
                    }
                }
            }
        }
    }