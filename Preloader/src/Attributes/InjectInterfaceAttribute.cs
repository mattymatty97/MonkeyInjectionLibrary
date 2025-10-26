using System;
using JetBrains.Annotations;

namespace InjectionLibrary.Attributes;

[MeansImplicitUse(ImplicitUseTargetFlags.WithMembers)]
[AttributeUsage(AttributeTargets.Interface, AllowMultiple = true)]
public sealed class InjectInterfaceAttribute(string typeName, string assemblyName = "Assembly-CSharp.dll") : Attribute
{
    public string AssemblyName => assemblyName;
    public string TypeName => typeName;
}