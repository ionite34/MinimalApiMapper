﻿// MinimalApiMapper.SourceGenerator/ApiMapperGenerator.cs

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using MinimalApiMapper.SourceGenerator.Extensions;
using MinimalApiMapper.SourceGenerator.Helpers;

namespace MinimalApiMapper.SourceGenerator;

#pragma warning disable RS1038
[Generator(LanguageNames.CSharp)]
#pragma warning restore RS1038
public class ApiMapperGenerator : IIncrementalGenerator
{
    // Define the fully qualified names of our attributes
    private const string MapGroupAttributeName = "MinimalApiMapper.Abstractions.MapGroupAttribute";
    private const string MapMethodAttributeBaseName =
        "MinimalApiMapper.Abstractions.MapMethodAttribute"; // Base class for GET, POST etc.
    private const string MapMethodsAttributeName =
        "MinimalApiMapper.Abstractions.MapMethodsAttribute"; // Specific attribute for MapMethods(...)

    public bool WriteToSource { get; set; } = true;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // --- Step 1: Find all classes potentially marked with [MapGroup] ---
        IncrementalValuesProvider<ClassDeclarationSyntax> classDeclarations = context
            .SyntaxProvider.CreateSyntaxProvider(
                predicate: static (s, _) => IsSyntaxTargetForGeneration(s),
                transform: static (ctx, _) => GetSemanticTargetForGeneration(ctx)
            )
            .Where(static m => m is not null)!;

        // --- Step 2: Combine class information with compilation ---
        IncrementalValueProvider<(
            Compilation,
            ImmutableArray<ClassDeclarationSyntax>
        )> compilationAndClasses = context.CompilationProvider.Combine(classDeclarations.Collect());

        // --- Step 3: Combine previous result with AnalyzerConfigOptions ---
        IncrementalValueProvider<(
            (Compilation, ImmutableArray<ClassDeclarationSyntax>),
            AnalyzerConfigOptionsProvider
        )> compilationClassesAndOptions = compilationAndClasses.Combine(
            context.AnalyzerConfigOptionsProvider
        );

        // --- Step 4: Register the source generation function ---
        context.RegisterSourceOutput(
            compilationClassesAndOptions,
            (spc, source) => Execute(source.Item1.Item1, source.Item1.Item2, source.Item2, spc)
        );
    }

    // --- Helper Methods for Initialization ---

    // Quick filter to check if a node is a ClassDeclarationSyntax
    private static bool IsSyntaxTargetForGeneration(SyntaxNode node) =>
        node is ClassDeclarationSyntax cdecl && cdecl.AttributeLists.Count > 0;

    // Deeper filter using semantic model to confirm the presence of [MapGroup]
    private static ClassDeclarationSyntax? GetSemanticTargetForGeneration(
        GeneratorSyntaxContext context
    )
    {
        var classDeclaration = (ClassDeclarationSyntax)context.Node;

        // Iterate through attributes on the class
        foreach (var attributeListSyntax in classDeclaration.AttributeLists)
        {
            foreach (var attributeSyntax in attributeListSyntax.Attributes)
            {
                if (
                    context.SemanticModel.GetSymbolInfo(attributeSyntax).Symbol
                    is IMethodSymbol attributeSymbol
                )
                {
                    var attributeContainingTypeSymbol = attributeSymbol.ContainingType;
                    var fullName = attributeContainingTypeSymbol.ToDisplayString();

                    // Check if the attribute is [MapGroup]
                    if (fullName == MapGroupAttributeName)
                    {
                        return classDeclaration;
                    }
                }
            }
        }
        return null; // Not the attribute we're looking for
    }

    // --- Main Execution Logic ---

    [SuppressMessage(
        "MicrosoftCodeAnalysisCorrectness",
        "RS1035:Do not use APIs banned for analyzers"
    )]
    private void Execute(
        Compilation compilation,
        ImmutableArray<ClassDeclarationSyntax> classes,
        AnalyzerConfigOptionsProvider optionsProvider,
        SourceProductionContext context
    )
    {
        if (classes.IsDefaultOrEmpty)
            return;

        var distinctClasses = classes
            .Select(c => compilation.GetSemanticModel(c.SyntaxTree).GetDeclaredSymbol(c))
            .Where(s => s is not null)
            .Distinct(SymbolEqualityComparer.Default)
            .OfType<INamedTypeSymbol>()
            .ToList();

        if (!distinctClasses.Any())
            return;

        // --- Get Output Path from Options Provider ---
        optionsProvider.GlobalOptions.TryGetValue(
            "build_property.MinimalApiMapper_GeneratedOutputFullPath",
            out var generatedOutputFullPath
        );

        var writeToDisk = WriteToSource && !string.IsNullOrWhiteSpace(generatedOutputFullPath);
        var writeSucceeded = false;

        // --- Collect Types for JSON Serialization ---
        var serializableTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        CollectSerializableTypes(distinctClasses, serializableTypes); // <-- Collect types first

        // --- Generate the Code Strings ---
        var (serviceExtensionCode, mappingExtensionCode) = GenerateMappingExtensions(
            compilation,
            distinctClasses,
            context
        ); // Don't need to pass serializableTypes here anymore

        string? jsonContextCode = null;
        string? jsonConfigCode = null;
        if (serializableTypes.Any())
        {
            jsonContextCode = GenerateJsonContext(serializableTypes, context);
            jsonConfigCode = GenerateJsonConfigurationExtension(context); // Context name is fixed now
        }

        // --- Write to Disk (if path provided) ---
        if (writeToDisk)
        {
            try
            {
                if (!Directory.Exists(generatedOutputFullPath))
                {
                    Directory.CreateDirectory(generatedOutputFullPath!);
                }

                // Write service extensions
                WriteOrDeleteFile(
                    generatedOutputFullPath!,
                    "MinimalApiMapper.ServiceExtensions.g.cs",
                    serviceExtensionCode
                );
                // Write mapping extensions
                WriteOrDeleteFile(
                    generatedOutputFullPath!,
                    "MinimalApiMapper.MappingExtensions.g.cs",
                    mappingExtensionCode
                );
                // Write JSON context (if generated)
                WriteOrDeleteFile(
                    generatedOutputFullPath!,
                    "MinimalApiMapper.JsonContext.g.cs",
                    jsonContextCode
                );
                // Write JSON config extension (if generated)
                WriteOrDeleteFile(
                    generatedOutputFullPath!,
                    "MinimalApiMapper.JsonExtensions.g.cs",
                    jsonConfigCode
                );

                writeSucceeded = true;
            }
            catch (Exception ex)
            {
                writeSucceeded = false;
                context.ReportDiagnostic(
                    Diagnostic.Create(
                        new DiagnosticDescriptor(
                            id: "MAM001",
                            title: "Failed to write generated file to disk",
                            messageFormat: "Failed to write generated file to '{0}'. Error: {1}",
                            category: "MinimalApiMapperGenerator",
                            DiagnosticSeverity.Warning, // Warning as AddSource still works
                            isEnabledByDefault: true
                        ),
                        Location.None, // No specific code location
                        messageArgs: [generatedOutputFullPath, ex.Message]
                    )
                );
            }
        }

        // --- Add Source to Compilation ONLY if NOT writing to disk successfully ---
        if (!writeSucceeded)
        {
            if (!string.IsNullOrEmpty(serviceExtensionCode))
                context.AddSource("MinimalApiMapper.ServiceExtensions.g.cs", serviceExtensionCode);
            if (!string.IsNullOrEmpty(mappingExtensionCode))
                context.AddSource("MinimalApiMapper.MappingExtensions.g.cs", mappingExtensionCode);
            if (!string.IsNullOrEmpty(jsonContextCode))
                context.AddSource("MinimalApiMapper.JsonContext.g.cs", jsonContextCode);
            if (!string.IsNullOrEmpty(jsonConfigCode))
                context.AddSource("MinimalApiMapper.JsonExtensions.g.cs", jsonConfigCode);
        }
    }

    // --- Helper to Write or Delete File ---
    [SuppressMessage(
        "MicrosoftCodeAnalysisCorrectness",
        "RS1035:Do not use APIs banned for analyzers"
    )]
    private static void WriteOrDeleteFile(string directory, string fileName, string? content)
    {
        var filePath = Path.Combine(directory, fileName);
        if (!string.IsNullOrEmpty(content))
        {
            File.WriteAllText(filePath, content);
        }
        else if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }
    }

    private static (
        string? ServiceExtensionCode,
        string? MappingExtensionCode
    ) GenerateMappingExtensions(
        Compilation compilation,
        List<INamedTypeSymbol> groupClasses,
        SourceProductionContext context
    )
    {
        // ... (Existing logic for generating service and mapping extensions) ...
        // ... (No changes needed inside this method itself) ...
        var serviceBuilder = new StringBuilder();
        var mappingBuilder = new StringBuilder();
        var requiredUsings = new HashSet<string>
        { /* Base Usings */
            "Microsoft.AspNetCore.Builder",
            "Microsoft.AspNetCore.Http",
            "Microsoft.AspNetCore.Routing",
            "Microsoft.Extensions.DependencyInjection",
            "System",
        };

        // Build service registration method header
        serviceBuilder.AppendLine("// <auto-generated/>\n#nullable enable");
        serviceBuilder.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        serviceBuilder.AppendLine("\nnamespace MinimalApiMapper.Generated;\n");
        serviceBuilder.AppendLine("public static class MinimalApiMapperServiceExtensions");
        serviceBuilder.AppendLine(
            "{\n    public static IServiceCollection AddApiGroups(this IServiceCollection services)\n    {"
        );

        // Loop through classes and methods to generate mappings...
        foreach (var groupClassSymbol in groupClasses)
        {
            var fullClassName = groupClassSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );
            requiredUsings.Add(
                groupClassSymbol.ContainingNamespace.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                )
            );
            serviceBuilder.AppendLine($"        services.AddScoped<{fullClassName}>();");

            var groupPrefix = GetGroupPrefix(groupClassSymbol);

            foreach (var member in groupClassSymbol.GetMembers())
            {
                if (
                    member is IMethodSymbol methodSymbol
                    && !methodSymbol.IsStatic
                    // && !methodSymbol.IsConstructor
                    && methodSymbol.DeclaredAccessibility == Accessibility.Public
                )
                {
                    var methodAttributes = GetMappingAttributes(methodSymbol);
                    if (!methodAttributes.Any())
                        continue;

                    foreach (var attrData in methodAttributes)
                    {
                        var mappingSnippet = GenerateMappingForMethod(
                            compilation,
                            groupClassSymbol,
                            methodSymbol,
                            attrData,
                            groupPrefix,
                            requiredUsings,
                            context
                        );
                        if (mappingSnippet != null)
                        {
                            mappingBuilder.AppendLine(mappingSnippet);
                            mappingBuilder.AppendLine();
                        }
                    }
                }
            }
        }

        serviceBuilder.AppendLine("        return services;\n    }\n}"); // Finish service method

        // Assemble final mapping code
        if (mappingBuilder.Length == 0)
            return (serviceBuilder.ToString(), null);

        var finalMappingCode = new StringBuilder();
        finalMappingCode.AppendLine("// <auto-generated/>\n#nullable enable");
        foreach (var ns in requiredUsings.OrderBy(u => u))
        {
            // Skip those with global
            if (ns.StartsWith("global::"))
            {
                continue;
            }

            finalMappingCode.AppendLine($"using {ns};");
        }

        finalMappingCode.AppendLine("\nnamespace MinimalApiMapper.Generated;\n");
        finalMappingCode.AppendLine("public static class MinimalApiMapperMappingExtensions");
        finalMappingCode.AppendLine(
            "{\n    public static IEndpointRouteBuilder MapApiGroups(this IEndpointRouteBuilder app)\n    {"
        );
        finalMappingCode.Append(mappingBuilder.ToString());
        finalMappingCode.AppendLine("        return app;\n    }\n}"); // Finish mapping method

        return (serviceBuilder.ToString(), finalMappingCode.ToString());
    }

    // --- JSON Context Generation ---
    private static string? GenerateJsonContext(
        HashSet<ITypeSymbol> serializableTypes,
        SourceProductionContext context
    )
    {
        if (!serializableTypes.Any())
            return null;

        var sb = new StringBuilder();
        var usings = new HashSet<string> { "System.Text.Json.Serialization" };

        sb.AppendLine("// <auto-generated/>\n#nullable enable"); // Header

        // Collect unique namespaces
        foreach (var type in serializableTypes.SelectMany(t => t.GetNamespacesForType()).Distinct())
        {
            usings.Add(type);
        }

        foreach (var ns in usings.OrderBy(u => u))
            sb.AppendLine($"using {ns};");
        sb.AppendLine("\nnamespace MinimalApiMapper.Generated;\n");

        // Generate [JsonSerializable] attributes
        foreach (var type in serializableTypes.OrderBy(t => t.Name))
        {
            // Ensure it's a class or struct (STJ requirement)
            if (
                type.TypeKind == TypeKind.Class
                || type.TypeKind == TypeKind.Struct
                || type.TypeKind == TypeKind.Enum
            )
            {
                sb.AppendLine(
                    $"[JsonSerializable(typeof({type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}))]"
                );
            }
        }

        // Define the context class
        sb.AppendLine(
            $"internal partial class MinimalApiMapperJsonContext : JsonSerializerContext"
        );
        sb.AppendLine("{ }"); // Empty body is sufficient

        return sb.ToString();
    }

    // --- JSON Configuration Extension ---
    // No longer needs serializableTypes parameter as context name is fixed
    private static string? GenerateJsonConfigurationExtension(SourceProductionContext context)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>\n#nullable enable"); // Header
        sb.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        sb.AppendLine("using Microsoft.AspNetCore.Http.Json;");
        sb.AppendLine("using System.Text.Json.Serialization;");
        sb.AppendLine("\nnamespace MinimalApiMapper.Generated;\n");
        sb.AppendLine("public static class MinimalApiMapperJsonExtensions");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>");
        sb.AppendLine(
            "    /// Configures System.Text.Json serialization options to use the generated MinimalApiMapperJsonContext"
        );
        sb.AppendLine(
            "    /// containing types discovered in MinimalApiMapper endpoints for Native AOT compatibility."
        );
        sb.AppendLine("    /// </summary>");
        sb.AppendLine(
            "    public static IServiceCollection AddApiGroupSerializers(this IServiceCollection services)"
        );
        sb.AppendLine("    {");
        sb.AppendLine("        services.Configure<JsonOptions>(options =>");
        sb.AppendLine("        {");
        sb.AppendLine(
            "            options.SerializerOptions.TypeInfoResolverChain.Add(MinimalApiMapperJsonContext.Default);"
        );
        sb.AppendLine("        });");
        sb.AppendLine("        return services;");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    // --- Add Back Type Collection Logic ---
    private static void CollectSerializableTypes(
        List<INamedTypeSymbol> groupClasses,
        HashSet<ITypeSymbol> serializableTypes
    )
    {
        foreach (var groupClass in groupClasses)
        {
            foreach (var member in groupClass.GetMembers())
            {
                if (
                    member is IMethodSymbol methodSymbol
                    && !methodSymbol.IsStatic
                    // && !methodSymbol.IsConstructor
                    && methodSymbol.DeclaredAccessibility == Accessibility.Public
                )
                {
                    // Only consider methods with mapping attributes
                    if (!GetMappingAttributes(methodSymbol).Any())
                        continue;

                    SerializableTypes.CollectSerializableTypesFromMethod(
                        methodSymbol,
                        serializableTypes
                    );
                }
            }
        }
    }

    // --- Code Generation Implementation ---

    private static (
        string? ServiceExtensionCode,
        string? MappingExtensionCode
    ) GenerateExtensionMethods(
        Compilation compilation,
        List<INamedTypeSymbol> groupClasses,
        SourceProductionContext context
    )
    {
        // Actual code generation logic here
        // 1. Iterating through groupClasses.
        // 2. For each class, find methods marked with MapMethodAttribute or MapMethodsAttribute.
        // 3. Extract route info, parameters, etc. using the compilation and semantic models.
        // 4. Build the strings for the AddApiGroups and MapApiGroups extension methods.
        // 5. Handle errors and report diagnostics using context.ReportDiagnostic(...).

        var serviceBuilder = new StringBuilder();
        var mappingBuilder = new StringBuilder();
        var requiredUsings = new HashSet<string>(); // Track usings needed for mapping

        // Start building the service registration extension method (File-scoped namespace)
        serviceBuilder.AppendLine("// <auto-generated/>"); // Header
        serviceBuilder.AppendLine("#nullable enable"); // Enable nullable reference types
        serviceBuilder.AppendLine("using Microsoft.Extensions.DependencyInjection;");
        serviceBuilder.AppendLine();
        serviceBuilder.AppendLine("namespace MinimalApiMapper.Generated;"); // File-scoped
        serviceBuilder.AppendLine();
        serviceBuilder.AppendLine("public static class MinimalApiMapperServiceExtensions");
        serviceBuilder.AppendLine("{");
        // Add generated code attribute
        serviceBuilder.AppendLine($"    {CodeBuilder.GetGeneratedCodeAttribute()}");
        serviceBuilder.AppendLine(
            "    public static IServiceCollection AddApiGroups(this IServiceCollection services)"
        );
        serviceBuilder.AppendLine("    {");

        // Start building the mapping extension method (File-scoped namespace)
        // mappingBuilder.AppendLine("// <auto-generated/>");
        // Add base usings - more will be added later dynamically
        requiredUsings.Add("Microsoft.AspNetCore.Builder");
        requiredUsings.Add("Microsoft.AspNetCore.Http");
        requiredUsings.Add("Microsoft.AspNetCore.Routing");
        requiredUsings.Add("Microsoft.Extensions.DependencyInjection");
        requiredUsings.Add("System"); // For IServiceProvider, Task etc.

        // --- Loop through discovered API Group classes ---
        foreach (var groupClassSymbol in groupClasses)
        {
            var fullClassName = groupClassSymbol.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            );
            requiredUsings.Add(groupClassSymbol.ContainingNamespace.ToDisplayString()); // Add namespace of the group class

            // Add service registration
            serviceBuilder.AppendLine($"        services.AddScoped<{fullClassName}>();");

            // Find the MapGroup attribute to get the prefix
            var groupPrefix = GetGroupPrefix(groupClassSymbol);

            // --- Loop through methods in the current group class ---
            foreach (var member in groupClassSymbol.GetMembers())
            {
                if (
                    member is IMethodSymbol methodSymbol
                    && !methodSymbol.IsStatic
                    // && !methodSymbol.IsConstructor
                    && methodSymbol.DeclaredAccessibility == Accessibility.Public
                )
                {
                    var methodAttributes = GetMappingAttributes(methodSymbol);
                    if (!methodAttributes.Any())
                        continue; // No mapping attributes found

                    foreach (var attrData in methodAttributes)
                    {
                        // Generate the mapping code snippet for this specific method and attribute
                        var mappingSnippet = GenerateMappingForMethod(
                            compilation,
                            groupClassSymbol,
                            methodSymbol,
                            attrData,
                            groupPrefix,
                            requiredUsings,
                            context
                        );

                        if (mappingSnippet != null)
                        {
                            mappingBuilder.AppendLine(mappingSnippet);
                            mappingBuilder.AppendLine(); // Add a blank line for readability
                        }
                    }
                }
            }
        }

        // Finish building the service method
        serviceBuilder.AppendLine("        return services;");
        serviceBuilder.AppendLine("    }");
        serviceBuilder.AppendLine("}");

        // Assemble the final mapping code with usings
        var finalMappingCode = new StringBuilder();
        finalMappingCode.AppendLine("// <auto-generated/>"); // Header
        finalMappingCode.AppendLine("#nullable enable"); // Enable nullable reference types
        foreach (var ns in requiredUsings.OrderBy(u => u))
        {
            // Skip those with global
            if (ns.StartsWith("global::"))
            {
                continue;
            }

            finalMappingCode.AppendLine($"using {ns};");
        }
        finalMappingCode.AppendLine();
        finalMappingCode.AppendLine("namespace MinimalApiMapper.Generated;"); // File-scoped
        finalMappingCode.AppendLine();
        finalMappingCode.AppendLine("public static class MinimalApiMapperMappingExtensions");
        finalMappingCode.AppendLine("{");
        // Add generated code attribute
        finalMappingCode.AppendLine($"    {CodeBuilder.GetGeneratedCodeAttribute()}");
        finalMappingCode.AppendLine(
            "    public static IEndpointRouteBuilder MapApiGroups(this IEndpointRouteBuilder app)"
        );
        finalMappingCode.AppendLine("    {");
        finalMappingCode.Append(mappingBuilder.ToString()); // Append the generated mappings
        finalMappingCode.AppendLine("        return app;");
        finalMappingCode.AppendLine("    }");
        finalMappingCode.AppendLine("}");

        // Return the generated code
        var finalServiceCode = groupClasses.Any() ? serviceBuilder.ToString() : null;
        var finalMappingCodeStr = mappingBuilder.Length > 0 ? finalMappingCode.ToString() : null;

        return (finalServiceCode, finalMappingCodeStr);
    }

    // --- Helper Methods for Generation ---

    private static string GetGroupPrefix(INamedTypeSymbol groupClassSymbol)
    {
        var mapGroupAttr = groupClassSymbol
            .GetAttributes()
            .FirstOrDefault(ad => ad.AttributeClass?.ToDisplayString() == MapGroupAttributeName);
        if (mapGroupAttr != null && mapGroupAttr.ConstructorArguments.Length > 0)
        {
            return mapGroupAttr.ConstructorArguments[0].Value?.ToString() ?? "";
        }
        return "";
    }

    private static List<AttributeData> GetMappingAttributes(IMethodSymbol methodSymbol)
    {
        return methodSymbol
            .GetAttributes()
            .Where(attr =>
                attr.AttributeClass != null
                && (
                    attr.AttributeClass.ToDisplayString() == MapMethodsAttributeName
                    || attr.AttributeClass.BaseType?.ToDisplayString() == MapMethodAttributeBaseName
                )
            )
            .ToList();
    }

    private static string? GenerateMappingForMethod(
        Compilation compilation,
        INamedTypeSymbol groupClassSymbol,
        IMethodSymbol methodSymbol,
        AttributeData mappingAttributeData,
        string groupPrefix,
        HashSet<string> requiredUsings,
        SourceProductionContext context
    )
    {
        // --- 1. Extract Mapping Attribute Info ---
        string? routeTemplate;
        var httpMethods = new List<string>();
        var mappingAttributeFullName = mappingAttributeData.AttributeClass!.ToDisplayString();

        if (mappingAttributeFullName == MapMethodsAttributeName)
        {
            if (mappingAttributeData.ConstructorArguments.Length < 2)
                return null; // Invalid attribute usage
            routeTemplate = mappingAttributeData.ConstructorArguments[0].Value?.ToString();
            var methodsArray = mappingAttributeData.ConstructorArguments[1].Values;
            if (methodsArray.IsDefaultOrEmpty)
                return null; // No methods specified
            httpMethods.AddRange(
                methodsArray
                    .Select(m => m.Value?.ToString()?.ToUpperInvariant())
                    .Where(m => m != null)!
            );
        }
        else // One of the MapGet, MapPost etc. attributes
        {
            if (mappingAttributeData.ConstructorArguments.Length < 1)
                return null; // Invalid attribute usage
            routeTemplate = mappingAttributeData.ConstructorArguments[0].Value?.ToString();
            // Infer HTTP method from attribute name
            var attributeShortName = mappingAttributeData.AttributeClass!.Name; // e.g., "MapGetAttribute"
            var httpMethod = attributeShortName
                .Substring(3)
                .Replace("Attribute", "")
                .ToUpperInvariant(); // e.g., "GET"
            httpMethods.Add(httpMethod);
        }

        if (!httpMethods.Any())
            return null; // No valid HTTP methods found

        // --- 2. Combine Route ---
        var finalRoute = CombineRoute(groupPrefix, routeTemplate);

        // --- 3. Generate Attributes for the Lambda ---
        var lambdaAttributesBuilder = new StringBuilder();
        foreach (var attributeData in methodSymbol.GetAttributes())
        {
            // Skip our mapping attributes
            var attrFullName = attributeData.AttributeClass!.ToDisplayString();
            if (
                attrFullName == MapMethodsAttributeName
                || attributeData.AttributeClass.BaseType?.ToDisplayString()
                    == MapMethodAttributeBaseName
            )
            {
                continue;
            }

            var attributeString = GenerateAttributeString(attributeData, requiredUsings);
            if (attributeString != null)
            {
                lambdaAttributesBuilder.Append(attributeString).Append(" "); // Add space after attribute
            }
        }
        var lambdaAttributes = lambdaAttributesBuilder.ToString();

        // --- 4. Analyze Parameters & Build Lambda Signature ---
        var lambdaParameters = new List<string>(); // e.g., "HttpContext context", "int id", "[FromBody] User user"
        var methodArguments = new List<string>(); // Arguments to pass to the actual method call

        // Instance methods still need HttpContext to resolve the group instance
        var needsContextForInstance = !methodSymbol.IsStatic;
        var contextParameterExists = methodSymbol.Parameters.Any(p =>
            p.Type.ToDisplayString() == "Microsoft.AspNetCore.Http.HttpContext"
        );

        if (needsContextForInstance && !contextParameterExists)
        {
            lambdaParameters.Add("HttpContext context");
            requiredUsings.Add("Microsoft.AspNetCore.Http");
        }

        foreach (var param in methodSymbol.Parameters)
        {
            var paramType = param.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            var paramName = param.Name;
            requiredUsings.Add(
                param.Type.ContainingNamespace.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                )
            ); // Ensure namespace is fully qualified

            var paramAttributesBuilder = new StringBuilder();
            foreach (var paramAttr in param.GetAttributes())
            {
                var attributeString = GenerateAttributeString(paramAttr, requiredUsings);
                if (attributeString != null)
                {
                    paramAttributesBuilder.Append(attributeString).Append(" ");
                }
            }

            // Add parameter with its attributes and fully qualified type
            lambdaParameters.Add($"{paramAttributesBuilder}{paramType} {paramName}");
            methodArguments.Add(paramName); // Add param name for the call later
        }

        var lambdaSignature = string.Join(", ", lambdaParameters);

        // --- 4. Build Lambda Body ---
        var bodyBuilder = new StringBuilder();
        // string groupInstanceVar = $"{groupClassSymbol.Name.ToLowerInvariant()}Instance";
        // var groupInstanceVar = $"{groupClassSymbol.Name.ToLowerInvariant()}_{Guid.NewGuid():N}"; // More unique name
        const string groupInstanceVar = "__group";
        var fullGroupClassName = groupClassSymbol.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
        );

        if (!methodSymbol.IsStatic)
        {
            var contextAccess = contextParameterExists
                ? methodSymbol
                    .Parameters.First(p =>
                        p.Type.ToDisplayString() == "Microsoft.AspNetCore.Http.HttpContext"
                    )
                    .Name
                : "context";
            bodyBuilder.AppendLine($"            var __sp = {contextAccess}.RequestServices;");
            bodyBuilder.AppendLine(
                $"            var {groupInstanceVar} = __sp.GetRequiredService<{fullGroupClassName}>();"
            );
        }

        var methodCallArgs = string.Join(", ", methodArguments);
        var methodCallTarget = methodSymbol.IsStatic ? fullGroupClassName : groupInstanceVar;
        var methodCall = $"{methodCallTarget}.{methodSymbol.Name}({methodCallArgs})";

        // Handle async methods
        var isAsync =
            methodSymbol.ReturnType is INamedTypeSymbol returnType
            && (
                returnType.GetFullName() == "System.Threading.Tasks.Task"
                || returnType.GetFullName() == "System.Threading.Tasks.ValueTask"
                || (
                    returnType.IsGenericType
                    && (
                        returnType.ConstructedFrom.GetFullName() == "System.Threading.Tasks.Task"
                        || returnType.ConstructedFrom.GetFullName()
                            == "System.Threading.Tasks.ValueTask"
                    )
                )
            );

        // Add return type namespace
        if (methodSymbol.ReturnType is INamedTypeSymbol namedReturn && !methodSymbol.ReturnsVoid)
        {
            requiredUsings.Add(
                namedReturn.ContainingNamespace.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat
                )
            );
            if (namedReturn.IsGenericType)
            {
                foreach (var typeArg in namedReturn.TypeArguments)
                {
                    if (typeArg is INamedTypeSymbol namedTypeArg)
                    {
                        requiredUsings.Add(
                            namedTypeArg.ContainingNamespace.ToDisplayString(
                                SymbolDisplayFormat.FullyQualifiedFormat
                            )
                        );
                    }
                }
            }
        }

        if (isAsync)
        {
            bodyBuilder.Append($"            return await {methodCall};");
        }
        else if (methodSymbol.ReturnsVoid)
        {
            bodyBuilder.AppendLine($"            {methodCall};");
            bodyBuilder.Append($"            return Results.Ok();"); // Default for void
            requiredUsings.Add("Microsoft.AspNetCore.Http"); // For Results
        }
        else // Synchronous method returning a value
        {
            bodyBuilder.Append($"            return {methodCall};");
        }

        // --- 6. Generate app.MapX Call ---
        var mapMethodName = DetermineMapMethod(httpMethods);
        var httpMethodsArg =
            mapMethodName == "MapMethods"
                ? $", new [] {{ {string.Join(", ", httpMethods.Select(m => $"\"{m}\""))} }}"
                : "";

        var lambdaModifier = isAsync ? "async " : "";

        var snippet = new StringBuilder();
        // Append method attributes before the lambda signature
        snippet.Append(
            $"        app.{mapMethodName}(\"{finalRoute}\", {lambdaAttributes}{lambdaModifier}({lambdaSignature}) =>"
        );
        snippet.AppendLine(); // Start lambda body on new line
        snippet.AppendLine("        {");
        snippet.AppendLine(bodyBuilder.ToString());
        snippet.Append("        })");
        // TODO: Add fluent API calls here later if needed (.WithName(), .WithTags(), etc.)
        snippet.Append(";");

        return snippet.ToString();
    }

    // --- New Helper Method for Generating Attribute Strings ---
    private static string? GenerateAttributeString(
        AttributeData attributeData,
        HashSet<string> requiredUsings
    )
    {
        if (attributeData.AttributeClass == null)
            return null;

        var attributeType = attributeData.AttributeClass;
        var fullAttributeTypeName = attributeType.ToDisplayString(
            SymbolDisplayFormat.FullyQualifiedFormat
        );
        requiredUsings.Add(
            attributeType.ContainingNamespace.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat
            )
        );

        var arguments = new List<string>();

        // Constructor arguments
        foreach (var arg in attributeData.ConstructorArguments)
        {
            arguments.Add(FormatTypedConstant(arg, requiredUsings));
        }

        // Named arguments
        foreach (var arg in attributeData.NamedArguments)
        {
            arguments.Add($"{arg.Key} = {FormatTypedConstant(arg.Value, requiredUsings)}");
        }

        var argsString = arguments.Any() ? $"({string.Join(", ", arguments)})" : "";
        return $"[{fullAttributeTypeName}{argsString}]";
    }

    // --- New Helper Method for Formatting TypedConstant Values ---
    private static string FormatTypedConstant(
        TypedConstant constant,
        HashSet<string> requiredUsings
    )
    {
        if (constant.IsNull)
            return "null";

        switch (constant.Kind)
        {
            case TypedConstantKind.Primitive:
                if (constant.Value is string s)
                    return $"\"{s.Replace("\"", "\\\"")}\""; // Escape quotes
                if (constant.Value is bool b)
                    return b ? "true" : "false";
                if (constant.Value is char c)
                    return $"'{c}'";
                // Add handling for other primitives if necessary (float, double literals)
                return constant.Value?.ToString() ?? "null";

            case TypedConstantKind.Enum:
                if (constant.Type is INamedTypeSymbol enumType)
                {
                    requiredUsings.Add(
                        enumType.ContainingNamespace.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat
                        )
                    );
                    // Find the enum member name matching the value
                    var memberName = enumType
                        .GetMembers()
                        .OfType<IFieldSymbol>()
                        .FirstOrDefault(f =>
                            f.ConstantValue != null && f.ConstantValue.Equals(constant.Value)
                        )
                        ?.Name;
                    if (memberName != null)
                    {
                        return $"{enumType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)}.{memberName}";
                    }
                }
                // Fallback to casting the underlying value
                return $"({constant.Type?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})({constant.Value})";

            case TypedConstantKind.Type:
                if (constant.Value is ITypeSymbol typeSymbol)
                {
                    requiredUsings.Add(
                        typeSymbol.ContainingNamespace.ToDisplayString(
                            SymbolDisplayFormat.FullyQualifiedFormat
                        )
                    );
                    return $"typeof({typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)})";
                }
                return "null"; // Should not happen?

            case TypedConstantKind.Array:
                var arrayValues = constant.Values.Select(v =>
                    FormatTypedConstant(v, requiredUsings)
                );
                var arrayTypeName = constant.Type is IArrayTypeSymbol ats
                    ? ats.ElementType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)
                    : "object"; // Fallback type
                requiredUsings.Add(
                    (
                        constant.Type as IArrayTypeSymbol
                    )?.ElementType.ContainingNamespace.ToDisplayString(
                        SymbolDisplayFormat.FullyQualifiedFormat
                    ) ?? "System"
                );
                return $"new {arrayTypeName}[] {{ {string.Join(", ", arrayValues)} }}";

            default:
                return constant.Value?.ToString() ?? "null"; // Fallback
        }
    }

    private static string CombineRoute(string prefix, string? template)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return template?.TrimStart('/') ?? "";
        }
        if (string.IsNullOrWhiteSpace(template))
        {
            return prefix;
        }
        return $"{prefix}/{template!.TrimStart('/')}";
    }

    private static string DetermineMapMethod(List<string> httpMethods)
    {
        if (httpMethods.Count == 1)
        {
            switch (httpMethods[0])
            {
                case "GET":
                    return "MapGet";
                case "POST":
                    return "MapPost";
                case "PUT":
                    return "MapPut";
                case "DELETE":
                    return "MapDelete";
                case "PATCH":
                    return "MapPatch";
            }
        }
        // Fallback for multiple methods or less common single methods
        return "MapMethods";
    }
}
