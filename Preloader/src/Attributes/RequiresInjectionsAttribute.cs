using System;

namespace InjectionLibrary.Attributes;

[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class RequiresInjectionsAttribute : Attribute;