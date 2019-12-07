using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace NUnit.Analyzers.Extensions
{
    internal static class AttributeArgumentSyntaxExtensions
    {
        internal static bool CanAssignTo(this AttributeArgumentSyntax @this, ITypeSymbol target, SemanticModel model,
            bool allowImplicitConversion = false,
            bool allowEnumToUnderlyingTypeConversion = false)
        {
            //See https://github.com/nunit/nunit/blob/f16d12d6fa9e5c879601ad57b4b24ec805c66054/src/NUnitFramework/framework/Attributes/TestCaseAttribute.cs#L396
            //for the reasoning behind this implementation.
            Optional<object> possibleConstantValue = model.GetConstantValue(@this.Expression);
            object argumentValue = null;
            if (possibleConstantValue.HasValue)
            {
                argumentValue = possibleConstantValue.Value;
            }
            TypeInfo sourceTypeInfo = model.GetTypeInfo(@this.Expression);
            ITypeSymbol argumentType = sourceTypeInfo.Type;

            if (argumentType == null)
            {
                return target.IsReferenceType ||
                    target.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T;
            }
            else
            {
                var targetType = GetTargetType(target);

                if (allowEnumToUnderlyingTypeConversion && targetType?.TypeKind == TypeKind.Enum)
                    targetType = (targetType as INamedTypeSymbol)?.EnumUnderlyingType;

                if (allowEnumToUnderlyingTypeConversion && argumentType?.TypeKind == TypeKind.Enum)
                    argumentType = (argumentType as INamedTypeSymbol)?.EnumUnderlyingType;

                if (targetType == null || argumentType == null)
                    return false;

                if (targetType.IsAssignableFrom(argumentType)
                    || (allowImplicitConversion && HasBuiltInImplicitConversion(argumentType, targetType, model)))
                {
                    return true;
                }
                else
                {
                    var canConvert = false;

                    if (targetType.SpecialType == SpecialType.System_Int16 || targetType.SpecialType == SpecialType.System_Byte ||
                        targetType.SpecialType == SpecialType.System_Int64 ||
                        targetType.SpecialType == SpecialType.System_SByte || targetType.SpecialType == SpecialType.System_Double)
                    {
                        canConvert = argumentType.SpecialType == SpecialType.System_Int32;
                    }
                    else if (targetType.SpecialType == SpecialType.System_Decimal)
                    {
                        canConvert = argumentType.SpecialType == SpecialType.System_Double ||
                            argumentType.SpecialType == SpecialType.System_String ||
                            argumentType.SpecialType == SpecialType.System_Int32;
                    }
                    else if (targetType.SpecialType == SpecialType.System_DateTime)
                    {
                        canConvert = argumentType.SpecialType == SpecialType.System_String;
                    }

                    if (canConvert)
                    {
                        return AttributeArgumentSyntaxExtensions.TryChangeType(targetType, argumentValue);
                    }

                    var reflectionTargetType = GetTargetReflectionType(targetType);
                    var reflectionArgumentType = GetTargetReflectionType(argumentType);

                    if (reflectionTargetType != null && reflectionArgumentType != null)
                    {
                        TypeConverter converter = TypeDescriptor.GetConverter(reflectionTargetType);
                        if (converter.CanConvertFrom(reflectionArgumentType))
                        {
                            try
                            {
                                converter.ConvertFrom(null, CultureInfo.InvariantCulture, argumentValue);
                                return true;
                            }
                            catch
                            {
                                return false;
                            }
                        }
                    }

                    return false;
                }
            }
        }

        private static ITypeSymbol GetTargetType(ITypeSymbol target)
        {
            if (target.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
                return (target as INamedTypeSymbol).TypeArguments.ToArray()[0];

            return target;
        }

        private static bool TryChangeType(ITypeSymbol targetType, object argumentValue)
        {
            Type targetReflectionType = GetTargetReflectionType(targetType);
            if (targetReflectionType == null)
            {
                return false;
            }

            try
            {
                Convert.ChangeType(argumentValue, targetReflectionType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (InvalidCastException)
            {
                return false;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        private static Type GetTargetReflectionType(ITypeSymbol targetType)
        {
            var containingAssembly = targetType.ContainingAssembly ?? targetType.BaseType.ContainingAssembly;
            string assembly = ", " + containingAssembly.Identity.ToString();
            string typeName = AttributeArgumentSyntaxExtensions.GetQualifiedTypeName(targetType);

            // First try to get type using assembly-qualified name, and if that fails try to get type
            // using only the type name qualified by its namespace.
            // This is a hacky attempt to make it work for types that are forwarded in .NET Core, e.g.
            // Double which exists in the System.Runtime assembly at design time and in
            // System.Private.CorLib at runtime, so targetType.ContainingAssembly will denote the wrong
            // assembly, System.Runtime. See e.g. the following comment
            // https://github.com/dotnet/roslyn/issues/16211#issuecomment-373084209
            var targetReflectionType = Type.GetType(typeName + assembly, false) ?? Type.GetType(typeName, false);
            return targetReflectionType;
        }

        private static string GetQualifiedTypeName(ITypeSymbol targetType)
        {
            // Note that this does not take into account generics,
            // so if that's ever added to attributes this will have to change.
            var namespaces = new Stack<string>();

            var @namespace = targetType.ContainingNamespace ?? targetType.BaseType.ContainingNamespace;
            var typeName = !string.IsNullOrEmpty(targetType.Name) ? targetType.Name : targetType.BaseType.Name;

            while (!@namespace.IsGlobalNamespace)
            {
                namespaces.Push(@namespace.Name);
                @namespace = @namespace.ContainingNamespace;
            }

            return $"{string.Join(".", namespaces)}.{typeName}";
        }

        private static bool HasBuiltInImplicitConversion(ITypeSymbol argumentType, ITypeSymbol targetType, SemanticModel model)
        {
            var conversion = model.Compilation.ClassifyConversion(argumentType, targetType);
            return conversion.IsImplicit && !conversion.IsUserDefined;
        }
    }
}
