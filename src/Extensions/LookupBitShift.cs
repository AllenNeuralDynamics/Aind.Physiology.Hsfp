using Bonsai;
using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;

[Combinator]
[Description("")]
[WorkflowElementCategory(ElementCategory.Transform)]
public class LookupBitShift
{
    public IObservable<int> Process(IObservable<int> source)
    {
        return source.Select(value => {
            for (int idx = 0; idx < 32; idx++)
            {
                if ((value & (1 << idx)) != 0)
                {
                    return idx;
                }
            }
            return -1;
        });
    }
}
