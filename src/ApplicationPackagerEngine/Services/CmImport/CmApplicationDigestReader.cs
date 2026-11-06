using System.Globalization;
using System.Xml;

namespace ApplicationPackagerEngine.Services.CmImport;

/// <summary>
/// Reads the stored document of a ConfigMgr application (SDMPackageXML) into a model. The
/// document holds the application and every deployment type. Priority is the order of the
/// deployment types. Read the application, not a deployment type: the deployment type cmdlet
/// returns the first deployment type's document for every type of an application.
/// </summary>
public static class CmApplicationDigestReader
{
    private static readonly Dictionary<string, string> OperatorMap = new(StringComparer.Ordinal)
    {
        ["Equals"] = "IsEquals", ["NotEquals"] = "NotEquals", ["GreaterThan"] = "GreaterThan",
        ["GreaterEquals"] = "GreaterEquals", ["GreaterThanOrEquals"] = "GreaterEquals",
        ["LessThan"] = "LessThan", ["LessEquals"] = "LessEquals", ["LessThanOrEquals"] = "LessEquals",
        ["BeginsWith"] = "BeginsWith", ["EndsWith"] = "EndsWith", ["Contains"] = "Contains",
        ["NotBeginsWith"] = "NotBeginsWith", ["NotEndsWith"] = "NotEndsWith", ["NotContains"] = "NotContains"
    };

    // ---- xml helpers ----------------------------------------------------

    private static IEnumerable<XmlElement> Children(XmlNode? node, string name) =>
        node is null ? [] : node.ChildNodes.OfType<XmlElement>().Where(e => e.LocalName == name);

    private static XmlElement? Child(XmlNode? node, string name) => Children(node, name).FirstOrDefault();

    private static string Text(XmlNode? node) => node?.InnerText ?? "";

    private static string Attr(XmlElement? node, string name) =>
        node is null || !node.HasAttribute(name) ? "" : node.GetAttribute(name);

    private static Dictionary<string, string> ArgMap(XmlElement? action)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var arg in Children(Child(action, "Args"), "Arg"))
        {
            var type = Attr(arg, "Type");
            map[Attr(arg, "Name")] = type.EndsWith("[]", StringComparison.Ordinal)
                ? string.Join(",", Children(arg, "Item").Select(Text))
                : Text(arg);
        }
        return map;
    }

    private static string Arg(Dictionary<string, string> map, string name, string fallback = "") =>
        map.TryGetValue(name, out var value) ? value : fallback;

    private static int ToInt(string text) => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    // ---- detection ------------------------------------------------------

    private sealed record Setting(string Source, bool Is64Bit, string Hive, string Key, string Path, string Filter, string ValueName, string DataType);

    private static Dictionary<string, Setting> ReadSettings(XmlElement? settingsNode)
    {
        var settings = new Dictionary<string, Setting>(StringComparer.Ordinal);
        if (settingsNode is null) return settings;
        foreach (var node in settingsNode.ChildNodes.OfType<XmlElement>())
        {
            var logical = Attr(node, "LogicalName");
            if (logical.Length == 0) continue;
            var dataType = Attr(node, "DataType");
            var is64 = Attr(node, "Is64Bit") == "true";
            settings[logical] = node.LocalName switch
            {
                "File" or "Folder" => new Setting(node.LocalName, is64, "", "", Text(Child(node, "Path")), Text(Child(node, "Filter")), "", dataType),
                "RegistryKey" => new Setting("RegistryKey", is64, Attr(node, "Hive"), Text(Child(node, "Key")), "", "", "", dataType),
                "SimpleSetting" when Child(node, "RegistryDiscoverySource") is { } source =>
                    new Setting("Registry", Attr(source, "Is64Bit") == "true", Attr(source, "Hive"), Text(Child(source, "Key")), "", "",
                        Text(Child(source, "ValueName")), dataType),
                _ => new Setting("Other", false, "", "", "", "", "", dataType)
            };
        }
        return settings;
    }

    private static DetectionNode ReadExpression(XmlElement expression, Dictionary<string, Setting> settings)
    {
        var op = Text(Child(expression, "Operator"));
        var operands = Child(expression, "Operands");
        if (op is "And" or "Or")
            return new DetectionGroup(op, Children(operands, "Expression").Select(e => ReadExpression(e, settings)).ToList());

        var reference = Child(operands, "SettingReference");
        var constant = Child(operands, "ConstantValue");
        var logical = Attr(reference, "SettingLogicalName");
        var sourceType = Attr(reference, "SettingSourceType");
        var method = Attr(reference, "Method");
        var property = Attr(reference, "PropertyPath");
        var dataType = Attr(reference, "DataType");
        var expected = Attr(constant, "Value");
        var mapped = OperatorMap.GetValueOrDefault(op, "");

        DetectionLeaf Leaf(string property2 = "Value", string oper = "", string expected2 = "", string path = "", string name = "",
            string hive = "", string key = "", bool is64 = false, bool supported = true, string reason = "") =>
            new(sourceType, property2, oper, expected2, dataType, path, name, hive, key, is64, supported, reason);

        settings.TryGetValue(logical, out var setting);
        if (setting is null || setting.Source == "Other" || mapped.Length == 0)
            return Leaf(oper: mapped, expected2: expected, supported: false,
                reason: $"Detection setting source '{sourceType}' with operator '{op}' is not converted.");

        switch (sourceType)
        {
            case "File" or "Folder":
                if (method == "Count")
                {
                    // "Count not equals 0" is existence; "count equals 0" is absence.
                    var exists = (mapped == "NotEquals" && expected == "0") || (mapped == "GreaterThan" && expected == "0");
                    var absent = mapped == "IsEquals" && expected == "0";
                    var ok = exists || absent;
                    return Leaf("Exists", "IsEquals", exists ? "True" : "False", setting.Path, setting.Filter, is64: setting.Is64Bit,
                        supported: ok, reason: ok ? "" : $"A {sourceType} count comparison other than 'exists' or 'does not exist' is not converted.");
                }
                return Leaf(property, mapped, expected, setting.Path, setting.Filter, is64: setting.Is64Bit);
            case "RegistryKey":
            {
                var valid = (mapped is "IsEquals" or "NotEquals") && expected.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || (mapped is "IsEquals" or "NotEquals") && expected.Equals("false", StringComparison.OrdinalIgnoreCase);
                var want = expected.Equals("true", StringComparison.OrdinalIgnoreCase);
                if (mapped == "NotEquals") want = !want;
                return Leaf("Exists", "IsEquals", want ? "True" : "False", hive: setting.Hive, key: setting.Key, is64: setting.Is64Bit,
                    supported: valid, reason: valid ? "" : "A registry key comparison other than exists or does not exist is not converted.");
            }
            case "Registry":
                return Leaf("Value", mapped, expected, name: setting.ValueName, hive: setting.Hive, key: setting.Key, is64: setting.Is64Bit);
            default:
                return Leaf(oper: mapped, expected2: expected, is64: setting.Is64Bit, supported: false,
                    reason: $"Detection setting source '{sourceType}' is not converted.");
        }
    }

    private static CmDetection ReadDetection(XmlElement custom, XmlElement installer)
    {
        var method = Text(Child(custom, "DetectionMethod"));
        switch (method)
        {
            case "Script":
            {
                var script = Child(custom, "DetectionScript");
                var args = ArgMap(Child(installer, "DetectAction"));
                return new CmDetection("Script", null, Text(script), Attr(script, "Language"), Arg(args, "RunAs32Bit", "false") == "true", "", "");
            }
            case "ProductCode":
                return new CmDetection("ProductCode", null, "", "", false, Text(Child(custom, "ProductCode")), Text(Child(custom, "ProductVersion")));
            case "Enhanced":
            {
                var enhanced = Child(custom, "EnhancedDetectionMethod");
                var settings = ReadSettings(Child(enhanced, "Settings"));
                var expression = Child(Child(enhanced, "Rule"), "Expression");
                return new CmDetection("Enhanced", expression is null ? null : ReadExpression(expression, settings), "", "", false, "", "");
            }
            default:
                return new CmDetection(method.Length > 0 ? method : "None", null, "", "", false, "", "");
        }
    }

    // ---- requirements, dependencies, supersedence -----------------------

    private static CmRequirementRule ReadRequirement(XmlElement rule)
    {
        var display = Attr(Child(Child(rule, "Annotation"), "DisplayName"), "Text");
        CmRequirementRule Unsupported(string reason) => new("Unsupported", display, "", [], "", [], "", reason);

        if (Child(rule, "OperatingSystemExpression") is { } os)
        {
            var ids = Children(Child(os, "Operands"), "RuleExpression").Select(e => Attr(e, "RuleId")).ToList();
            return new CmRequirementRule("OsPlatform", display, Text(Child(os, "Operator")), ids, "", [], "", "");
        }

        var expression = Child(rule, "Expression");
        if (expression is null) return Unsupported("The requirement has no expression.");
        var op = Text(Child(expression, "Operator"));
        var operands = Child(expression, "Operands");
        var global = Child(operands, "GlobalSettingReference");
        if (global is null || op is "And" or "Or")
            return Unsupported("The requirement uses a custom setting or a compound expression.");

        IReadOnlyList<string> values = [];
        if (Child(operands, "ConstantValue") is { } constant) values = [Attr(constant, "Value")];
        if (Child(operands, "ConstantValueList") is { } list)
            values = Children(list, "ConstantValue").Select(c => Attr(c, "Value")).ToList();

        if (Attr(global, "AuthoringScopeId") != "GLOBAL")
            return new CmRequirementRule("Unsupported", display, op, [], Attr(global, "LogicalName"), values, Attr(global, "DataType"),
                "The requirement uses a custom global condition.");
        return new CmRequirementRule("Global", display, op, [], Attr(global, "LogicalName"), values, Attr(global, "DataType"), "");
    }

    private static List<CmReferenceGroup> ReadReferences(XmlElement? container)
    {
        var groups = new List<CmReferenceGroup>();
        foreach (var rule in Children(container, "DeploymentTypeRule"))
        {
            var groupName = Attr(Child(Child(rule, "Annotation"), "DisplayName"), "Text");
            var expression = Child(rule, "DeploymentTypeExpression");
            var op = "";
            List<XmlElement> intents;
            if (expression is not null)
            {
                op = Text(Child(expression, "Operator"));
                intents = Children(Child(expression, "Operands"), "DeploymentTypeIntentExpression").ToList();
            }
            else
            {
                intents = Children(rule, "DeploymentTypeIntentExpression").ToList();
            }
            var targets = intents.Select(intent => new CmReferenceTarget
            {
                DesiredState = Attr(intent, "DesiredState"),
                ApplicationLogical = Attr(Child(intent, "DeploymentTypeApplicationReference"), "LogicalName"),
                DeploymentTypeLogical = Attr(Child(intent, "DeploymentTypeReference"), "LogicalName")
            }).ToList();
            groups.Add(new CmReferenceGroup(groupName, op, targets));
        }
        return groups;
    }

    // ---- the application ------------------------------------------------

    /// <summary>Parses the stored document. Throws <see cref="InvalidDataException"/> for any other document.</summary>
    public static CmApplication Read(string xml)
    {
        var doc = new XmlDocument { XmlResolver = null };
        doc.LoadXml(xml.TrimStart('﻿'));
        var root = doc.DocumentElement;
        if (root is null || root.LocalName != "AppMgmtDigest")
            throw new InvalidDataException("The document is not a ConfigMgr application digest (AppMgmtDigest).");

        var app = Child(root, "Application");
        var info = Child(Child(app, "DisplayInfo"), "Info");
        string First(string a, string b) => a.Length > 0 ? a : b;
        var title = First(Text(Child(app, "Title")), Text(Child(info, "Title")));
        var publisher = First(Text(Child(app, "Publisher")), Text(Child(info, "Publisher")));
        var version = First(Text(Child(app, "SoftwareVersion")), Text(Child(info, "Version")));
        var icon = Child(info, "Icon") is { } iconNode ? Text(Child(iconNode, "Data")) : "";

        // The application lists its deployment types in priority order. The definitions follow in the
        // same order in a stored document; the list decides if they ever differ.
        var definitions = Children(root, "DeploymentType").ToList();
        var order = Children(Child(app, "DeploymentTypes"), "DeploymentType").Select(e => Attr(e, "LogicalName")).ToList();
        if (order.Count > 0)
            definitions = definitions.OrderBy(d => { var p = order.IndexOf(Attr(d, "LogicalName")); return p < 0 ? int.MaxValue : p; }).ToList();

        var deploymentTypes = new List<CmDeploymentType>();
        var priority = 0;
        foreach (var dtNode in definitions)
        {
            priority++;
            var installer = Child(dtNode, "Installer");
            var technology = Text(Child(dtNode, "Technology"));
            var unsupported = technology is "MSI" or "Script" ? ""
                : $"The deployment technology '{technology}' is not converted. Intune Win32 apps carry Windows Installer and script installers.";

            string executionContext = "System", requiresLogOn = "", post = "BasedOnExitCode", installCommand = "", uninstallCommand = "",
                uninstallSetting = "", contentId = "", uninstallContentId = "", contentLocation = "";
            bool interaction = false, runAs32 = false;
            int maxRuntime = 0, estimated = 0;
            IReadOnlyList<CmExitCode> exitCodes = [];
            IReadOnlyList<string> contentFiles = [];
            CmDetection? detection = null;

            if (installer is not null)
            {
                var custom = Child(installer, "CustomData");
                var actionArgs = ArgMap(Child(installer, "InstallAction"));
                executionContext = Arg(actionArgs, "ExecutionContext", "System");
                requiresLogOn = Arg(actionArgs, "RequiresLogOn");
                interaction = Arg(actionArgs, "RequiresUserInteraction", "false") == "true";
                post = Arg(actionArgs, "PostInstallBehavior", "BasedOnExitCode");
                maxRuntime = ToInt(Arg(actionArgs, "MaxExecuteTime"));
                estimated = ToInt(Arg(actionArgs, "ExecuteTime"));
                runAs32 = Arg(actionArgs, "RunAs32Bit", "false") == "true";

                if (custom is not null)
                {
                    installCommand = Text(Child(custom, "InstallCommandLine"));
                    uninstallCommand = Text(Child(custom, "UninstallCommandLine"));
                    uninstallSetting = Text(Child(custom, "UninstallSetting"));
                    contentId = Attr(Child(custom, "InstallContent"), "ContentId");
                    uninstallContentId = Attr(Child(custom, "UninstallContent"), "ContentId");
                    exitCodes = Children(Child(custom, "ExitCodes"), "ExitCode").Select(e => new CmExitCode(ToInt(Attr(e, "Code")), Attr(e, "Class"))).ToList();
                    detection = ReadDetection(custom, installer);
                }

                foreach (var content in Children(Child(installer, "Contents"), "Content"))
                {
                    if (contentId.Length > 0 && Attr(content, "ContentId") != contentId) continue;
                    contentLocation = Text(Child(content, "Location")).TrimEnd('\\');
                    contentFiles = Children(content, "File").Select(f => Attr(f, "Name")).ToList();
                    break;
                }
            }

            deploymentTypes.Add(new CmDeploymentType
            {
                Name = Text(Child(dtNode, "Title")),
                LogicalName = Attr(dtNode, "LogicalName"),
                Priority = priority,
                Technology = technology,
                UnsupportedReason = unsupported,
                ExecutionContext = executionContext,
                RequiresLogOn = requiresLogOn,
                RequiresUserInteraction = interaction,
                PostInstallBehavior = post,
                MaxRuntimeMins = maxRuntime,
                EstimatedRuntimeMins = estimated,
                RunAs32Bit = runAs32,
                InstallCommand = installCommand,
                UninstallCommand = uninstallCommand,
                UninstallSetting = uninstallSetting,
                ContentLocation = contentLocation,
                ContentId = contentId,
                UninstallContentId = uninstallContentId,
                ContentFiles = contentFiles,
                ExitCodes = exitCodes,
                Detection = detection,
                Requirements = Children(Child(dtNode, "Requirements"), "Rule").Select(ReadRequirement).ToList(),
                Dependencies = ReadReferences(Child(dtNode, "Dependencies")),
                Supersedes = ReadReferences(Child(dtNode, "Supersedes"))
            });
        }

        return new CmApplication
        {
            Name = title, Publisher = publisher, Version = version,
            Description = Text(Child(info, "Description")),
            LogicalName = Attr(app, "LogicalName"),
            IconBase64 = icon,
            DeploymentTypes = deploymentTypes
        };
    }
}
