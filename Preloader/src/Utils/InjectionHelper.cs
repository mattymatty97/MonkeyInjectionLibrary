using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using InjectionLibrary.Attributes;
using InjectionLibrary.Exceptions;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using MonoMod.Utils;
using CustomAttributeNamedArgument = Mono.Cecil.CustomAttributeNamedArgument;
using EventAttributes = Mono.Cecil.EventAttributes;
using FieldAttributes = Mono.Cecil.FieldAttributes;
using ICustomAttributeProvider = Mono.Cecil.ICustomAttributeProvider;
using MethodAttributes = Mono.Cecil.MethodAttributes;
using ParameterAttributes = Mono.Cecil.ParameterAttributes;
using PropertyAttributes = Mono.Cecil.PropertyAttributes;

namespace InjectionLibrary.Utils;

internal static class InjectionHelper
{
    private static readonly string ErrorStrategyAttributeName = typeof(HandleErrorsAttribute).FullName;
    
    private const MethodAttributes InterfaceMethodAttributes = 
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot;
    private const MethodAttributes InterfaceSpecialMethodAttributes = 
        MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot |
        MethodAttributes.SpecialName | MethodAttributes.HideBySig;
    private const PropertyAttributes InterfacePropertyAttributes = 
        PropertyAttributes.None;
    private const EventAttributes InterfaceEventAttributes = 
        EventAttributes.None;

    public static T GetAttributeInstance<T>(this CustomAttribute attribute) where T : Attribute
    {
        var attrType = typeof(T);
        var constructorArgs = attribute.ConstructorArguments.Select(ca => ca.Value).ToArray();
        return (T)Activator.CreateInstance(attrType, constructorArgs);
    }

    private static void HandleError(string message, ErrorHandlingStrategy strategy)
    {
        switch (strategy)
        {
            default:
            case ErrorHandlingStrategy.Terminate:
                Preloader.Log.LogFatal(message);
                throw new TerminationException("Fatal Injection error!");
            case ErrorHandlingStrategy.LogWarning:
                Preloader.Log.LogWarning(message);
                break;
            case ErrorHandlingStrategy.LogError:
                Preloader.Log.LogFatal(message);
                break;
            case ErrorHandlingStrategy.Ignore:
                break;
        }
    }

    internal static void ImplementInterface(this TypeDefinition self, TypeReference @interface, ErrorHandlingStrategy errorHandlingStrategy)
    {
        var definition = @interface.Resolve();
        
        var attribute = definition.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == ErrorStrategyAttributeName);
        if (attribute != null)
        {
            errorHandlingStrategy = attribute.GetAttributeInstance<HandleErrorsAttribute>().Strategy;
        }

        if (!definition.IsInterface)
            HandleError($"Type '{@interface.FullName}' is not an interface!", errorHandlingStrategy);
        
        if (definition.HasGenericParameters)
            HandleError($"Type '{@interface.FullName}' is a Generic type!", errorHandlingStrategy);

        Preloader.Log.LogDebug($"Injecting '{@interface.FullName}' into {self.FullName}'");

        //import it in the target assembly
        var newRef = self.Module.ImportReference(@interface);

        var blacklist = new HashSet<IMemberDefinition>();

        Interfaces.ImplementProperties(self, definition, blacklist, errorHandlingStrategy);

        Interfaces.ImplementEvents(self, definition, blacklist, errorHandlingStrategy);

        Interfaces.ImplementMethods(self, definition, blacklist, errorHandlingStrategy);

        self.Interfaces.Add(new InterfaceImplementation(newRef));
    }

    private static class Interfaces
    {
        internal static void ImplementProperties(in TypeDefinition type, TypeDefinition @interface,
            in HashSet<IMemberDefinition> blacklist,
            ErrorHandlingStrategy errorHandlingStrategy)
        {
            foreach (var property in @interface.Properties)
            {
                ImplementProperty(type, property, blacklist, errorHandlingStrategy);
            }
        }

        internal static void ImplementEvents(in TypeDefinition type, TypeDefinition @interface,
            in HashSet<IMemberDefinition> blacklist,
            ErrorHandlingStrategy errorHandlingStrategy)
        {
            foreach (var @event in @interface.Events)
            {
                ImplementEvent(type, @event, blacklist, errorHandlingStrategy);
            }
        }

        internal static void ImplementMethods(in TypeDefinition type, TypeDefinition @interface,
            in HashSet<IMemberDefinition> blacklist,
            ErrorHandlingStrategy errorHandlingStrategy)
        {
            foreach (var method in @interface.Methods)
            {
                ImplementMethod(type, method, blacklist, errorHandlingStrategy);
            }
        }

        private static void ImplementProperty(in TypeDefinition type, PropertyDefinition property,
            in HashSet<IMemberDefinition> blacklist,
            ErrorHandlingStrategy errorHandlingStrategy)
        {
            PropertyDefinition implementation = null;
            FieldDefinition backingField = null;

            var hasImplementation = false;

            if (!blacklist.Add(property))
                return;

            Preloader.Log.LogDebug($"Adding Property  '{property.Name}' to {type.FullName}'");
            
            var attribute = property.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == ErrorStrategyAttributeName);
            if (attribute != null)
            {
                errorHandlingStrategy = attribute.GetAttributeInstance<HandleErrorsAttribute>().Strategy;
            }
                
            var typeRef = type.Module.ImportReference(property.PropertyType);

            if (property.GetMethod is { IsAbstract: true } && blacklist.Add(property.GetMethod))
            {
                Preloader.Log.LogDebug($"Adding getter of '{property.Name}' to {type.FullName}'");

                var getMethod = type.FindMethod($"get_{property.Name}");
                if ( getMethod != null)
                {
                    HandleError($"Method 'get_{property.Name}' is already defined in '{type.FullName}'", errorHandlingStrategy);
                    getMethod.Attributes = InterfaceSpecialMethodAttributes;
                }
                else
                {
                    Init(type, out implementation);

                    getMethod = new MethodDefinition(
                        $"get_{property.Name}",
                        InterfaceSpecialMethodAttributes,
                        typeRef
                    );
                    
                    getMethod.MarkAsInjected(type.Module);

                    var ilProcessor = getMethod.Body.GetILProcessor();
                    ilProcessor.Emit(OpCodes.Ldarg_0);
                    ilProcessor.Emit(OpCodes.Ldfld, backingField);
                    ilProcessor.Emit(OpCodes.Ret);
                    type.Methods.Add(getMethod);
                    implementation.GetMethod = getMethod;
                }

                getMethod.CopyCustomAttributes(property.GetMethod);
            }

            if (property.SetMethod is { IsAbstract: true } && blacklist.Add(property.SetMethod))
            {
                Preloader.Log.LogDebug($"Adding setter of '{property.Name}' to {type.FullName}'");
                
                var setMethod = type.FindMethod($"set_{property.Name}");
                if (setMethod != null)
                {
                    HandleError($"Method 'set_{property.Name}' is already defined in '{type.FullName}'", errorHandlingStrategy);
                    setMethod.Attributes = InterfaceSpecialMethodAttributes;
                }
                else
                {
                    if (!hasImplementation)
                        Init(type, out implementation);

                    setMethod = new MethodDefinition(
                        $"set_{property.Name}",
                        InterfaceSpecialMethodAttributes,
                        type.Module.TypeSystem.Void
                    );
                    
                    setMethod.MarkAsInjected(type.Module);

                    setMethod.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, typeRef));
                    var ilProcessor = setMethod.Body.GetILProcessor();
                    ilProcessor.Emit(OpCodes.Ldarg_0);
                    ilProcessor.Emit(OpCodes.Ldarg_1);
                    ilProcessor.Emit(OpCodes.Stfld, backingField);
                    ilProcessor.Emit(OpCodes.Ret);
                    type.Methods.Add(setMethod);
                    implementation.SetMethod = setMethod;
                }

                setMethod.CopyCustomAttributes(property.SetMethod);
            }

            if (!hasImplementation)
            {
                Preloader.Log.LogDebug($"Default Property '{property.Name}' to {type.FullName}'");
                return;
            }

            // add backing field and implementation if they have been created
            type.Fields.TryAdd(backingField);
            type.Properties.TryAdd(implementation);
            
            return;

            void Init(in TypeDefinition @type, out PropertyDefinition propertyImpl)
            {
                propertyImpl = null;

                //create the backing field
                backingField = type.FindField($"<{property.Name}>k__BackingField");
                if (backingField == null)
                {
                    backingField = new FieldDefinition(
                        $"<{property.Name}>k__BackingField",
                        FieldAttributes.Private,
                        typeRef
                    );
                    
                    backingField.MarkAsInjected(type.Module);
                }
                else
                    HandleError($"Field '<{property.Name}>k__BackingField' already exists in {type.FullName}", errorHandlingStrategy);
                
                // Create the property
                propertyImpl = type.FindProperty(property.Name);
                if (propertyImpl == null)
                {
                    propertyImpl = new PropertyDefinition(property.Name, InterfacePropertyAttributes, typeRef);
                    
                    propertyImpl.CopyCustomAttributes(property);
                    
                    propertyImpl.MarkAsInjected(type.Module);
                }
                else
                    HandleError($"Property '{property.Name}' already exists in {type.FullName}", errorHandlingStrategy);

                hasImplementation = true;
            }
        }

        private static void ImplementEvent(in TypeDefinition type, EventDefinition @event,
            in HashSet<IMemberDefinition> blacklist,
            ErrorHandlingStrategy errorHandlingStrategy)
        {
            EventDefinition implementation = null;
            FieldDefinition backingField = null;

            var hasImplementation = false;

            if (!blacklist.Add(@event))
                return;

            Preloader.Log.LogDebug($"Adding Event     '{@event.Name}' to {type.FullName}'");
            
            var attribute = @event.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == ErrorStrategyAttributeName);
            if (attribute != null)
            {
                errorHandlingStrategy = attribute.GetAttributeInstance<HandleErrorsAttribute>().Strategy;
            }
                
            var typeRef = type.Module.ImportReference(@event.EventType);

            if (@event.AddMethod is { IsAbstract: true } && blacklist.Add(@event.AddMethod))
            {

                Preloader.Log.LogDebug($"Adding add of    '{@event.Name}' to {type.FullName}'");
                var addMethod = type.FindMethod($"add_{@event.Name}");
                
                if (addMethod != null)
                {
                    HandleError($"Method 'add_{@event.Name}' is already defined in '{type.FullName}'", errorHandlingStrategy);
                    addMethod.Attributes = InterfaceSpecialMethodAttributes;
                }
                else
                {
                    Init(type, out implementation);

                    addMethod = new MethodDefinition(
                        $"add_{@event.Name}",
                        InterfaceSpecialMethodAttributes,
                        type.Module.TypeSystem.Void
                    );
                    
                    addMethod.MarkAsInjected(type.Module);

                    addMethod.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, typeRef));
                    var ilProcessor = addMethod.Body.GetILProcessor();
                    ilProcessor.Emit(OpCodes.Ldarg_0);
                    ilProcessor.Emit(OpCodes.Ldarg_0);
                    ilProcessor.Emit(OpCodes.Ldfld, backingField);
                    ilProcessor.Emit(OpCodes.Ldarg_1);
                    ilProcessor.Emit(OpCodes.Call,
                        type.Module.ImportReference(typeof(Delegate).GetMethod("Combine",
                            [typeof(Delegate), typeof(Delegate)])));
                    ilProcessor.Emit(OpCodes.Castclass, @event.EventType);
                    ilProcessor.Emit(OpCodes.Stfld, backingField);
                    ilProcessor.Emit(OpCodes.Ret);
                    type.Methods.Add(addMethod);
                    implementation.AddMethod = addMethod;
                }

                addMethod.CopyCustomAttributes(@event.AddMethod);
            }

            if (@event.RemoveMethod is { IsAbstract: true } && blacklist.Add(@event.RemoveMethod))
            {
                if (!hasImplementation)
                    Init(type, out implementation);

                Preloader.Log.LogDebug($"Adding remove of '{@event.Name}' to {type.FullName}'");
                
                var removeMethod = type.FindMethod($"remove_{@event.Name}");
                if (removeMethod != null)
                {
                    HandleError($"Method 'remove_{@event.Name}' is already defined in '{type.FullName}'", errorHandlingStrategy);
                    removeMethod.Attributes = InterfaceSpecialMethodAttributes;
                }
                else
                {
                    removeMethod = new MethodDefinition(
                        $"remove_{@event.Name}",
                        InterfaceSpecialMethodAttributes,
                        type.Module.TypeSystem.Void
                    );
                    
                    removeMethod.MarkAsInjected(type.Module);

                    removeMethod.Parameters.Add(new ParameterDefinition("value", ParameterAttributes.None, typeRef));
                    var ilProcessor = removeMethod.Body.GetILProcessor();
                    ilProcessor.Emit(OpCodes.Ldarg_0);
                    ilProcessor.Emit(OpCodes.Ldarg_0);
                    ilProcessor.Emit(OpCodes.Ldfld, backingField);
                    ilProcessor.Emit(OpCodes.Ldarg_1);
                    ilProcessor.Emit(OpCodes.Call,
                        type.Module.ImportReference(typeof(Delegate).GetMethod("Remove",
                            [typeof(Delegate), typeof(Delegate)])));
                    ilProcessor.Emit(OpCodes.Castclass, @event.EventType);
                    ilProcessor.Emit(OpCodes.Stfld, backingField);
                    ilProcessor.Emit(OpCodes.Ret);
                    type.Methods.Add(removeMethod);
                    implementation.RemoveMethod = removeMethod;
                }

                removeMethod.CopyCustomAttributes(@event.RemoveMethod);
            }

            if (@event.InvokeMethod is { IsAbstract: true } && blacklist.Add(@event.InvokeMethod))
            {

                Preloader.Log.LogDebug($"Adding raise of  '{@event.Name}' to {type.FullName}'");

                var invokeMethod = type.FindMethod($"raise_{@event.Name}");
                if (invokeMethod != null)
                {
                    HandleError($"Method 'raise_{@event.Name}' is already defined in '{type.FullName}'", errorHandlingStrategy);
                    invokeMethod.Attributes = InterfaceSpecialMethodAttributes;
                }
                else
                {
                    if (!hasImplementation)
                        Init(type, out implementation);
                    
                    invokeMethod = new MethodDefinition(
                        $"raise_{@event.Name}",
                        InterfaceSpecialMethodAttributes,
                        type.Module.TypeSystem.Void
                    );
                    
                    invokeMethod.MarkAsInjected(type.Module);

                    // Invoke method parameters depend on the delegate signature
                    foreach (var param in @event.InvokeMethod.Parameters)
                    {
                        invokeMethod.Parameters.Add(new ParameterDefinition(param.Name, param.Attributes,
                            type.Module.ImportReference(param.ParameterType)));
                    }

                    var ilProcessor = invokeMethod.Body.GetILProcessor();
                    ilProcessor.Emit(OpCodes.Ldarg_0);
                    ilProcessor.Emit(OpCodes.Ldfld, backingField);
                    for (var i = 0; i < invokeMethod.Parameters.Count; i++)
                    {
                        ilProcessor.Emit(OpCodes.Ldarg, i + 1);
                    }

                    ilProcessor.Emit(OpCodes.Callvirt,
                        type.Module.ImportReference(@event.EventType.Resolve().Methods.First(m => m.Name == "Invoke")));
                    ilProcessor.Emit(OpCodes.Ret);
                    type.Methods.Add(invokeMethod);
                    implementation.InvokeMethod = invokeMethod;
                }

                invokeMethod.CopyCustomAttributes(@event.InvokeMethod);
            }

            if (!hasImplementation)
                return;

            // add backing field and implementation if they have been created
            type.Fields.TryAdd(backingField);
            type.Events.TryAdd(implementation);

            return;

            void Init(in TypeDefinition @type, out EventDefinition eventImpl)
            {
                eventImpl = null;

                //create the backing field
                backingField = type.FindField(@event.Name);
                if (backingField == null)
                {
                    backingField = new FieldDefinition(
                        @event.Name,
                        FieldAttributes.Private,
                        typeRef
                    );
                    
                    backingField.MarkAsInjected(type.Module);
                }
                else 
                    HandleError($"Field '{@event.Name}' already exists in {type.FullName}", errorHandlingStrategy);

                // Create the property
                eventImpl = type.FindEvent(@event.Name);
                if (eventImpl == null)
                {
                    eventImpl = new EventDefinition(@event.Name, InterfaceEventAttributes, typeRef);
                    
                    eventImpl.CopyCustomAttributes(@event);
                    
                    eventImpl.MarkAsInjected(type.Module);
                }
                else
                    HandleError($"Event '{@event.Name}' already exists in {type.FullName}", errorHandlingStrategy);
                

                hasImplementation = true;
            }
        }

        private static void ImplementMethod(in TypeDefinition type, MethodDefinition method,
            in HashSet<IMemberDefinition> blacklist,
            ErrorHandlingStrategy errorHandlingStrategy)
        {
            if (!blacklist.Add(method))
                return;

            Preloader.Log.LogDebug($"Adding Method    '{method.Name}' to {type.FullName}'");

            //only implement methods that do not have a default implementation!
            if (!method.IsAbstract)
                return;
            
            var attribute = method.CustomAttributes.FirstOrDefault(a => a.AttributeType.FullName == ErrorStrategyAttributeName);
            if (attribute != null)
            {
                errorHandlingStrategy = attribute.GetAttributeInstance<HandleErrorsAttribute>().Strategy;
            }

            var implementation = type.FindMethod(method.Name);
            if (implementation != null)
            {
                HandleError($"Method '{method.Name}' is already defined in '{type.FullName}'", errorHandlingStrategy);

                implementation.Attributes = InterfaceMethodAttributes;
            }
            else
            {
                // Create a new method to implement the interface method
                implementation = new MethodDefinition(
                    method.Name,
                    InterfaceMethodAttributes,
                    type.Module.ImportReference(method.ReturnType)
                );

                // Copy parameters
                foreach (var param in method.Parameters)
                {
                    implementation.Parameters.Add(
                        new ParameterDefinition(param.Name, param.Attributes, type.Module.ImportReference(param.ParameterType)));
                }

                // Create method body (simple return or throw for now)
                var il = implementation.Body.GetILProcessor();

                if (method.ReturnType.FullName != "System.Void")
                {
                    var constructorInfo = typeof(NotImplementedException).GetConstructor([typeof(string)]);
                    var constructorReference = type.Module.ImportReference(constructorInfo);
                    il.Emit(OpCodes.Ldstr, "This is a Stub");
                    il.Emit(OpCodes.Newobj, constructorReference);
                    il.Emit(OpCodes.Throw);
                }
                else
                {
                    il.Emit(OpCodes.Ret);
                }
                
                implementation.MarkAsInjected(type.Module);
            }
            
            implementation.CopyCustomAttributes(method);
            // Add the method to the type
            type.Methods.TryAdd(implementation);
        }
    }

    private static void MarkAsInjected<T>(this T target, ModuleDefinition module) where T : MemberReference, ICustomAttributeProvider
    {
        // Import the attribute type from your current assembly
        var attributeTypeRef = module.ImportReference(typeof(InjectedMemberAttribute));

        // Get the constructor you want to use
        var attributeCtor = module.ImportReference(typeof(InjectedMemberAttribute).GetConstructor([]));

        // Create the custom attribute
        var customAttribute = new CustomAttribute(attributeCtor);
        
        target.CustomAttributes.Add(customAttribute);
    }
    
    private static void CopyCustomAttributes<T>(this T target, T source) where T : MemberReference, ICustomAttributeProvider
    {
        var targetModule = target.Module;
    
        foreach (var attribute in source.CustomAttributes)
        {
            if (attribute.AttributeType.FullName == ErrorStrategyAttributeName)
                continue;
            
            // Import the attribute constructor into the target module
            var attributeConstructor = targetModule.ImportReference(attribute.Constructor);
        
            // Create a new custom attribute
            var newAttribute = new CustomAttribute(attributeConstructor);
        
            // Copy constructor arguments
            foreach (var arg in attribute.ConstructorArguments)
            {
                newAttribute.ConstructorArguments.Add(
                    targetModule.ImportCustomAttributeArgument(arg)
                );
            }
        
            // Copy named arguments (properties/fields)
            foreach (var namedArg in attribute.Properties)
            {
                newAttribute.Properties.Add(new CustomAttributeNamedArgument(
                    namedArg.Name,
                    targetModule.ImportCustomAttributeArgument(namedArg.Argument)
                ));
            }
        
            foreach (var namedArg in attribute.Fields)
            {
                newAttribute.Fields.Add(new CustomAttributeNamedArgument(
                    namedArg.Name,
                    targetModule.ImportCustomAttributeArgument(namedArg.Argument)
                ));
            }
        
            target.CustomAttributes.Add(newAttribute);
        }
    }
    
    private static CustomAttributeArgument ImportCustomAttributeArgument(this ModuleDefinition targetModule, CustomAttributeArgument arg)
    {
        var importedType = targetModule.ImportReference(arg.Type);

        switch (arg.Value)
        {
            // Handle different argument value types
            case TypeReference typeRef:
                // Type arguments (e.g., typeof(SomeType)) need to be imported
                return new CustomAttributeArgument(
                    importedType,
                    targetModule.ImportReference(typeRef)
                );
            case CustomAttributeArgument[] array:
            {
                // Handle array values - each element might contain TypeReferences
                var importedArray = new CustomAttributeArgument[array.Length];
                for (int i = 0; i < array.Length; i++)
                {
                    importedArray[i] = ImportCustomAttributeArgument(targetModule, array[i]);
                }
                return new CustomAttributeArgument(importedType, importedArray);
            }
            default:
                // Primitive values (strings, ints, enums, etc.) can be copied directly
                return new CustomAttributeArgument(importedType, arg.Value);
        }
    }
    
    private static void TryAdd<T>(this Collection<T> collection, T newMember) where T : IMemberDefinition
    {
        if (collection.Any(member => member.Name == newMember.Name))
            return;
        collection.Add(newMember);
    }
    
}