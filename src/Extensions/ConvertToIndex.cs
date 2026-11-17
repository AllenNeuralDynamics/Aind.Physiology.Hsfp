using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using AllenNeuralDynamics.Cuttlefish;

[Combinator]
[Description("")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class ConvertToIndex
{
    public IObservable<int> Process(IObservable<Pins> source)
    {
        return source.Select(value => {
            value = value & (Pins) 254; // remove pin 0 
            return pinToIndex[value]; 
            });
    }

    private static readonly Dictionary<Pins, int> pinToIndex = new Dictionary<Pins, int>
    {
        { Pins.Pin1, 0},
        { Pins.Pin2, 1},
        { Pins.Pin3, 2},
        { Pins.Pin4, 3},
        { Pins.Pin5, 4},
        { Pins.Pin6, 5},
        { Pins.Pin7, 6},
    };
}
