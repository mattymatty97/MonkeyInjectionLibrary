using System;
using JetBrains.Annotations;

namespace InjectionLibrary.Attributes;

[UsedImplicitly(ImplicitUseTargetFlags.Members)]
[AttributeUsage(validOn: AttributeTargets.Assembly | AttributeTargets.Interface | AttributeTargets.Property | AttributeTargets.Method)]
public class HandleErrorsAttribute : Attribute
{
    public HandleErrorsAttribute(ErrorHandlingStrategy strategy)
    {
        Strategy = strategy;
    }

    public HandleErrorsAttribute(int strategy)
    {
        Strategy = (ErrorHandlingStrategy)strategy;
    }

    public ErrorHandlingStrategy Strategy { get; }
}