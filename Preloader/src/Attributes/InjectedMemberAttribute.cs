using System;

namespace InjectionLibrary.Attributes;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Field | AttributeTargets.Property | AttributeTargets.Event,
    AllowMultiple = true)]
internal class InjectedMemberAttribute() : Attribute;