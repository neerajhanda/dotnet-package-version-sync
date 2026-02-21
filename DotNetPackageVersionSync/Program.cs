using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using NuGet.Versioning;

internal static class Program
{
    private static readonly StringComparer PackageComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex SlnProjectLineRegex = new(
        "^Project\\(\"[^\"]+\"\\)\\s*=\\s*\"[^\"]+\"\\s*,\\s*\"([^\"]+)\"\\s*,",
        RegexOptions.Compiled);

    private static int Main(string[] args)
    {
        CliOptions options;

        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            PrintUsage();
            return 1;
        }

        if (options.ShowHelp)
        {
            PrintUsage();
            return 0;
        }

        string solutionPath;

        try
        {
            solutionPath = ResolveSolutionPath(options.SolutionPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var projectPaths = ReadProjectPaths(solutionPath).ToList();

        if (projectPaths.Count == 0)
        {
            Console.WriteLine($"No SDK-style project files found in {solutionPath}.");
            return 0;
        }

        var packagesConfigProjects = FindPackagesConfigProjects(projectPaths);
        if (packagesConfigProjects.Count > 0)
        {
            Console.Error.WriteLine("Unsupported dependency format detected: packages.config.");
            Console.Error.WriteLine("dotnet-package-version-sync currently supports only PackageReference-based projects.");
            Console.Error.WriteLine("Migrate these projects to PackageReference before running this tool:");

            foreach (var project in packagesConfigProjects.OrderBy(item => item.ProjectPath, StringComparer.OrdinalIgnoreCase))
            {
                Console.Error.WriteLine($"- {project.ProjectPath} (packages.config: {project.PackagesConfigPath})");
            }

            return 1;
        }

        var projects = projectPaths.Select(ProjectFile.Load).ToList();
        var packageRefs = projects.SelectMany(project => project.PackageReferences).ToList();

        if (packageRefs.Count == 0)
        {
            Console.WriteLine("No PackageReference entries were found.");
            return 0;
        }

        var groupedPackages = packageRefs
            .GroupBy(packageRef => packageRef.PackageId, PackageComparer)
            .Select(group => PackageGroupInfo.From(group.Key, group.ToList()))
            .ToList();

        Console.WriteLine($"Analyzed {projects.Count} projects and {packageRefs.Count} package references.");

        var unresolvedGroups = groupedPackages.Where(group => group.PreferredVersion is null).ToList();
        if (unresolvedGroups.Count > 0)
        {
            foreach (var group in unresolvedGroups)
            {
                var versions = group.ExplicitVersionSpecs.Count == 0
                    ? "<none>"
                    : string.Join(", ", group.ExplicitVersionSpecs.OrderBy(spec => spec, PackageComparer));
                Console.WriteLine($"Skipping package '{group.PackageId}' because versions cannot be reconciled: {versions}");
            }
        }

        if (options.Centralize)
        {
            return RunCentralization(options, solutionPath, projects, groupedPackages);
        }

        return RunAlignment(options, projects, groupedPackages);
    }

    private static int RunAlignment(CliOptions options, List<ProjectFile> projects, List<PackageGroupInfo> groupedPackages)
    {
        var commonPackageVersions = groupedPackages
            .Where(group => group.ReferencedByProjectCount >= 2)
            .Where(group => group.PreferredVersion is not null)
            .ToDictionary(group => group.PackageId, group => group.PreferredVersion!, PackageComparer);

        if (commonPackageVersions.Count == 0)
        {
            Console.WriteLine("No common packages with reconcilable versions were found.");
            return 0;
        }

        var changes = new List<PackageChange>();

        foreach (var project in projects)
        {
            foreach (var packageRef in project.PackageReferences)
            {
                if (!commonPackageVersions.TryGetValue(packageRef.PackageId, out var targetVersion))
                {
                    continue;
                }

                var currentVersion = packageRef.GetVersionSpec();
                if (VersionsEquivalent(currentVersion, targetVersion))
                {
                    continue;
                }

                if (!options.DryRun)
                {
                    packageRef.SetVersionSpec(targetVersion);
                    project.MarkChanged();
                }

                changes.Add(new PackageChange(project.ProjectPath, packageRef.PackageId, currentVersion, targetVersion, "aligned"));
            }
        }

        var savedProjectCount = SaveProjects(projects, options.DryRun);

        Console.WriteLine(options.DryRun
            ? $"[Dry run] {changes.Count} package reference(s) would be aligned across {commonPackageVersions.Count} common package(s)."
            : $"Aligned {changes.Count} package reference(s) across {commonPackageVersions.Count} common package(s).");

        if (!options.DryRun)
        {
            Console.WriteLine($"Updated {savedProjectCount} project file(s).");
        }

        PrintChanges(changes, options.DryRun);
        return 0;
    }

    private static int RunCentralization(
        CliOptions options,
        string solutionPath,
        List<ProjectFile> projects,
        List<PackageGroupInfo> groupedPackages)
    {
        var existingCentralVersions = LoadExistingCentralPackageVersions(solutionPath);

        var effectiveGroups = groupedPackages
            .Select(group =>
            {
                if (group.PreferredVersion is not null)
                {
                    return group;
                }

                return existingCentralVersions.TryGetValue(group.PackageId, out var centralVersion)
                    ? group with { PreferredVersion = centralVersion }
                    : group;
            })
            .ToList();

        var unresolved = effectiveGroups
            .Where(group => group.ReferencedByProjectCount >= 2)
            .Where(group => group.PreferredVersion is null)
            .ToList();

        if (unresolved.Count > 0)
        {
            Console.Error.WriteLine("Centralization stopped because some multi-project packages have incompatible version specs.");
            foreach (var group in unresolved)
            {
                Console.Error.WriteLine($"- {group.PackageId}: {string.Join(", ", group.ExplicitVersionSpecs.OrderBy(spec => spec, PackageComparer))}");
            }

            return 1;
        }

        var packageVersions = effectiveGroups
            .Where(group => group.PreferredVersion is not null)
            .ToDictionary(group => group.PackageId, group => group.PreferredVersion!, PackageComparer);

        if (packageVersions.Count == 0)
        {
            Console.WriteLine("No package versions could be resolved for central package management.");
            return 0;
        }

        var changes = new List<PackageChange>();

        foreach (var project in projects)
        {
            foreach (var packageRef in project.PackageReferences)
            {
                if (!packageVersions.TryGetValue(packageRef.PackageId, out var targetVersion))
                {
                    continue;
                }

                var currentVersion = packageRef.GetVersionSpec();
                if (currentVersion is null)
                {
                    continue;
                }

                if (!options.DryRun)
                {
                    if (packageRef.RemoveVersionSpec())
                    {
                        project.MarkChanged();
                    }
                }

                changes.Add(new PackageChange(project.ProjectPath, packageRef.PackageId, currentVersion, targetVersion, "centralized"));
            }
        }

        var propsPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "Directory.Packages.props");

        var centralResult = options.DryRun
            ? CentralPropsUpdateResult.ForDryRun(propsPath, packageVersions)
            : UpdateCentralPackagesProps(propsPath, packageVersions);

        var savedProjectCount = SaveProjects(projects, options.DryRun);

        Console.WriteLine(options.DryRun
            ? $"[Dry run] {changes.Count} package reference version(s) would be removed from projects and {packageVersions.Count} package version(s) would be managed centrally."
            : $"Centralized {packageVersions.Count} package version(s) and removed {changes.Count} project-level version(s).");

        if (!options.DryRun)
        {
            Console.WriteLine($"Updated {savedProjectCount} project file(s). Central package file: {centralResult.Path}");
            Console.WriteLine($"Directory.Packages.props changes: {centralResult.AddedOrUpdatedCount} added/updated entries.");
        }

        PrintChanges(changes, options.DryRun);
        return 0;
    }

    private static Dictionary<string, string> LoadExistingCentralPackageVersions(string solutionPath)
    {
        var propsPath = Path.Combine(Path.GetDirectoryName(solutionPath)!, "Directory.Packages.props");
        if (!File.Exists(propsPath))
        {
            return new Dictionary<string, string>(PackageComparer);
        }

        var doc = XDocument.Load(propsPath);
        var versions = new Dictionary<string, string>(PackageComparer);

        foreach (var packageVersion in doc.Descendants().Where(element => element.Name.LocalName == "PackageVersion"))
        {
            var packageId = packageVersion.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(packageId))
            {
                continue;
            }

            var version = packageVersion.Attribute("Version")?.Value;
            if (string.IsNullOrWhiteSpace(version))
            {
                version = packageVersion.Elements().FirstOrDefault(child => child.Name.LocalName == "Version")?.Value;
            }

            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            versions[packageId.Trim()] = version.Trim();
        }

        return versions;
    }

    private static int SaveProjects(IEnumerable<ProjectFile> projects, bool dryRun)
    {
        var saved = 0;
        foreach (var project in projects)
        {
            if (!project.IsChanged)
            {
                continue;
            }

            saved++;
            if (!dryRun)
            {
                project.Save();
            }
        }

        return saved;
    }

    private static void PrintChanges(List<PackageChange> changes, bool dryRun)
    {
        if (changes.Count == 0)
        {
            Console.WriteLine(dryRun
                ? "[Dry run] No edits would be made."
                : "No file edits were required.");
            return;
        }

        foreach (var change in changes
                     .OrderBy(change => change.ProjectPath, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(change => change.PackageId, PackageComparer))
        {
            var from = change.FromVersion ?? "<none>";
            Console.WriteLine($"{Path.GetFileName(change.ProjectPath)}: {change.PackageId} {from} -> {change.ToVersion} ({change.Action})");
        }
    }

    private static CentralPropsUpdateResult UpdateCentralPackagesProps(string propsPath, Dictionary<string, string> packageVersions)
    {
        XDocument doc;

        if (File.Exists(propsPath))
        {
            doc = XDocument.Load(propsPath, LoadOptions.PreserveWhitespace);
        }
        else
        {
            doc = new XDocument(new XElement("Project"));
        }

        var root = doc.Root ?? throw new InvalidOperationException("Directory.Packages.props is missing the Project root element.");

        var propertyGroup = root.Elements().FirstOrDefault(element => element.Name.LocalName == "PropertyGroup");
        if (propertyGroup is null)
        {
            propertyGroup = new XElement(root.GetDefaultNamespace() + "PropertyGroup");
            root.AddFirst(propertyGroup);
        }

        var manageCentral = propertyGroup.Elements().FirstOrDefault(element => element.Name.LocalName == "ManagePackageVersionsCentrally");
        if (manageCentral is null)
        {
            manageCentral = new XElement(root.GetDefaultNamespace() + "ManagePackageVersionsCentrally", "true");
            propertyGroup.Add(manageCentral);
        }
        else
        {
            manageCentral.Value = "true";
        }

        var packageVersionElements = root
            .Descendants()
            .Where(element => element.Name.LocalName == "PackageVersion")
            .Where(element => element.Attribute("Include") is not null)
            .ToDictionary(
                element => element.Attribute("Include")!.Value,
                element => element,
                PackageComparer);

        var itemGroup = root.Elements().FirstOrDefault(element => element.Name.LocalName == "ItemGroup" &&
                                                                  element.Elements().Any(child => child.Name.LocalName == "PackageVersion"));
        if (itemGroup is null)
        {
            itemGroup = new XElement(root.GetDefaultNamespace() + "ItemGroup");
            root.Add(itemGroup);
        }

        var changedCount = 0;
        foreach (var package in packageVersions.OrderBy(item => item.Key, PackageComparer))
        {
            if (packageVersionElements.TryGetValue(package.Key, out var existing))
            {
                var versionAttr = existing.Attribute("Version");
                if (!string.Equals(versionAttr?.Value, package.Value, StringComparison.OrdinalIgnoreCase))
                {
                    existing.SetAttributeValue("Version", package.Value);
                    changedCount++;
                }
            }
            else
            {
                itemGroup.Add(new XElement(
                    root.GetDefaultNamespace() + "PackageVersion",
                    new XAttribute("Include", package.Key),
                    new XAttribute("Version", package.Value)));
                changedCount++;
            }
        }

        var settings = new XmlWriterSettings
        {
            Indent = true,
            OmitXmlDeclaration = false,
            NewLineChars = Environment.NewLine,
            NewLineHandling = NewLineHandling.Replace
        };

        using (var writer = XmlWriter.Create(propsPath, settings))
        {
            doc.Save(writer);
        }

        return new CentralPropsUpdateResult(propsPath, changedCount);
    }

    private static bool VersionsEquivalent(string? left, string right)
    {
        if (left is null)
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var parsedLeft = TryParseComparableVersion(left, out var leftVersion);
        var parsedRight = TryParseComparableVersion(right, out var rightVersion);

        return parsedLeft && parsedRight && leftVersion == rightVersion;
    }

    private static bool TryParseComparableVersion(string value, out NuGetVersion version)
    {
        if (NuGetVersion.TryParse(value, out version!))
        {
            return true;
        }

        if (VersionRange.TryParse(value, out var range) &&
            range.HasLowerBound &&
            range.HasUpperBound &&
            range.IsMinInclusive &&
            range.IsMaxInclusive &&
            range.MinVersion == range.MaxVersion)
        {
            version = range.MinVersion;
            return true;
        }

        version = default!;
        return false;
    }

    private static string ResolveSolutionPath(string? providedPath)
    {
        if (string.IsNullOrWhiteSpace(providedPath))
        {
            return ResolveSolutionFromDirectory(Directory.GetCurrentDirectory());
        }

        var fullPath = Path.GetFullPath(providedPath);

        if (Directory.Exists(fullPath))
        {
            return ResolveSolutionFromDirectory(fullPath);
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Solution path was not found: {fullPath}");
        }

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The --solution path must point to a .sln or .slnx file.");
        }

        return fullPath;
    }

    private static string ResolveSolutionFromDirectory(string directory)
    {
        var candidates = Directory
            .GetFiles(directory, "*.sln")
            .Concat(Directory.GetFiles(directory, "*.slnx"))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        if (candidates.Count == 0)
        {
            throw new FileNotFoundException($"No .sln or .slnx file found in {directory}. Provide one with --solution.");
        }

        throw new InvalidOperationException($"Multiple solution files were found in {directory}. Provide one with --solution.");
    }

    private static IEnumerable<string> ReadProjectPaths(string solutionPath)
    {
        var extension = Path.GetExtension(solutionPath);
        var solutionDir = Path.GetDirectoryName(solutionPath)!;

        return extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
            ? ReadProjectsFromSlnx(solutionPath, solutionDir)
            : ReadProjectsFromSln(solutionPath, solutionDir);
    }

    private static IEnumerable<string> ReadProjectsFromSlnx(string solutionPath, string solutionDir)
    {
        var doc = XDocument.Load(solutionPath);

        var projects = doc
            .Descendants()
            .Where(element => element.Name.LocalName == "Project")
            .Select(element => element.Attribute("Path")?.Value)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.GetFullPath(Path.Combine(solutionDir, path!)))
            .Where(IsBuildProject)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return projects;
    }

    private static IEnumerable<string> ReadProjectsFromSln(string solutionPath, string solutionDir)
    {
        var lines = File.ReadLines(solutionPath);
        var projects = new List<string>();

        foreach (var line in lines)
        {
            var match = SlnProjectLineRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var relativePath = match.Groups[1].Value;
            var fullPath = Path.GetFullPath(Path.Combine(solutionDir, relativePath));
            if (!IsBuildProject(fullPath))
            {
                continue;
            }

            projects.Add(fullPath);
        }

        return projects.Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsBuildProject(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        return extension.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".fsproj", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".vbproj", StringComparison.OrdinalIgnoreCase);
    }

    private static List<PackagesConfigProject> FindPackagesConfigProjects(IEnumerable<string> projectPaths)
    {
        var projects = new List<PackagesConfigProject>();

        foreach (var projectPath in projectPaths)
        {
            var packagesConfigPath = TryFindPackagesConfigPath(projectPath);
            if (packagesConfigPath is null)
            {
                continue;
            }

            projects.Add(new PackagesConfigProject(projectPath, packagesConfigPath));
        }

        return projects;
    }

    private static string? TryFindPackagesConfigPath(string projectPath)
    {
        var projectDirectory = Path.GetDirectoryName(projectPath);
        if (string.IsNullOrWhiteSpace(projectDirectory))
        {
            return null;
        }

        var defaultPath = Path.Combine(projectDirectory, "packages.config");
        if (File.Exists(defaultPath))
        {
            return defaultPath;
        }

        try
        {
            var doc = XDocument.Load(projectPath);
            var includeValue = doc
                .Descendants()
                .Attributes()
                .Where(attribute =>
                    attribute.Name.LocalName is "Include" or "Update" or "Remove")
                .Select(attribute => attribute.Value)
                .FirstOrDefault(value => value.EndsWith("packages.config", StringComparison.OrdinalIgnoreCase));

            if (string.IsNullOrWhiteSpace(includeValue))
            {
                return null;
            }

            return Path.GetFullPath(Path.Combine(projectDirectory, includeValue));
        }
        catch
        {
            return null;
        }
    }

    private static void PrintUsage()
    {
        Console.WriteLine("dotnet-package-version-sync");
        Console.WriteLine("Align NuGet package versions across projects in a solution.");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run --project DotNetPackageVersionSync -- [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -s, --solution <path>   Path to a .sln/.slnx file or a directory containing one.");
        Console.WriteLine("  -c, --centralize        Enable Central Package Management using Directory.Packages.props.");
        Console.WriteLine("  -n, --dry-run           Show what would change without writing files.");
        Console.WriteLine("  -h, --help              Show this help text.");
    }

    private sealed record CliOptions(string? SolutionPath, bool Centralize, bool DryRun, bool ShowHelp)
    {
        public static CliOptions Parse(string[] args)
        {
            string? solutionPath = null;
            var centralize = false;
            var dryRun = false;
            var showHelp = false;

            for (var index = 0; index < args.Length; index++)
            {
                var arg = args[index];

                switch (arg)
                {
                    case "-h":
                    case "--help":
                        showHelp = true;
                        break;
                    case "-c":
                    case "--centralize":
                        centralize = true;
                        break;
                    case "-n":
                    case "--dry-run":
                        dryRun = true;
                        break;
                    case "-s":
                    case "--solution":
                        if (index + 1 >= args.Length)
                        {
                            throw new ArgumentException("--solution requires a value.");
                        }

                        solutionPath = args[++index];
                        break;
                    default:
                        if (arg.StartsWith("-", StringComparison.Ordinal))
                        {
                            throw new ArgumentException($"Unknown option: {arg}");
                        }

                        if (solutionPath is null)
                        {
                            solutionPath = arg;
                        }
                        else
                        {
                            throw new ArgumentException($"Unexpected argument: {arg}");
                        }

                        break;
                }
            }

            return new CliOptions(solutionPath, centralize, dryRun, showHelp);
        }
    }

    private sealed class ProjectFile
    {
        public string ProjectPath { get; }
        public XDocument Document { get; }
        public List<PackageReferenceInfo> PackageReferences { get; }
        public bool IsChanged { get; private set; }

        private ProjectFile(string projectPath, XDocument document, List<PackageReferenceInfo> packageReferences)
        {
            ProjectPath = projectPath;
            Document = document;
            PackageReferences = packageReferences;
        }

        public static ProjectFile Load(string projectPath)
        {
            var doc = XDocument.Load(projectPath, LoadOptions.PreserveWhitespace);
            var packageReferences = doc
                .Descendants()
                .Where(element => element.Name.LocalName == "PackageReference")
                .Select(element => PackageReferenceInfo.FromElement(projectPath, element))
                .Where(reference => reference is not null)
                .Cast<PackageReferenceInfo>()
                .ToList();

            return new ProjectFile(projectPath, doc, packageReferences);
        }

        public void MarkChanged()
        {
            IsChanged = true;
        }

        public void Save()
        {
            Document.Save(ProjectPath, SaveOptions.DisableFormatting);
        }
    }

    private sealed class PackageReferenceInfo
    {
        private readonly XElement _element;

        public string ProjectPath { get; }
        public string PackageId { get; }

        private PackageReferenceInfo(string projectPath, string packageId, XElement element)
        {
            ProjectPath = projectPath;
            PackageId = packageId;
            _element = element;
        }

        public static PackageReferenceInfo? FromElement(string projectPath, XElement element)
        {
            var id = element.Attribute("Include")?.Value ?? element.Attribute("Update")?.Value;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            return new PackageReferenceInfo(projectPath, id.Trim(), element);
        }

        public string? GetVersionSpec()
        {
            var versionAttr = _element.Attribute("Version")?.Value;
            if (!string.IsNullOrWhiteSpace(versionAttr))
            {
                return versionAttr.Trim();
            }

            var versionElement = _element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version");
            if (versionElement is not null && !string.IsNullOrWhiteSpace(versionElement.Value))
            {
                return versionElement.Value.Trim();
            }

            return null;
        }

        public void SetVersionSpec(string version)
        {
            var versionAttr = _element.Attribute("Version");
            if (versionAttr is not null)
            {
                versionAttr.Value = version;
                return;
            }

            var versionElement = _element.Elements().FirstOrDefault(child => child.Name.LocalName == "Version");
            if (versionElement is not null)
            {
                versionElement.Value = version;
                return;
            }

            _element.SetAttributeValue("Version", version);
        }

        public bool RemoveVersionSpec()
        {
            var changed = false;

            var versionAttr = _element.Attribute("Version");
            if (versionAttr is not null)
            {
                versionAttr.Remove();
                changed = true;
            }

            var versionElements = _element.Elements().Where(child => child.Name.LocalName == "Version").ToList();
            foreach (var versionElement in versionElements)
            {
                versionElement.Remove();
                changed = true;
            }

            return changed;
        }
    }

    private sealed record PackageGroupInfo(
        string PackageId,
        int ReferencedByProjectCount,
        IReadOnlyCollection<string> ExplicitVersionSpecs,
        string? PreferredVersion)
    {
        public static PackageGroupInfo From(string packageId, List<PackageReferenceInfo> references)
        {
            var projectCount = references
                .Select(reference => reference.ProjectPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            var versions = references
                .Select(reference => reference.GetVersionSpec())
                .Where(version => !string.IsNullOrWhiteSpace(version))
                .Select(version => version!)
                .Distinct(PackageComparer)
                .ToList();

            var preferredVersion = ResolvePreferredVersion(versions);

            return new PackageGroupInfo(packageId, projectCount, versions, preferredVersion);
        }

        private static string? ResolvePreferredVersion(List<string> versions)
        {
            if (versions.Count == 0)
            {
                return null;
            }

            if (versions.Count == 1)
            {
                return versions[0];
            }

            var comparableVersions = new List<NuGetVersion>();

            foreach (var versionSpec in versions)
            {
                if (!TryParseComparableVersion(versionSpec, out var parsed))
                {
                    return null;
                }

                comparableVersions.Add(parsed);
            }

            var selected = comparableVersions.Max();
            return selected?.ToNormalizedString();
        }
    }

    private sealed record PackageChange(
        string ProjectPath,
        string PackageId,
        string? FromVersion,
        string ToVersion,
        string Action);

    private sealed record CentralPropsUpdateResult(string Path, int AddedOrUpdatedCount)
    {
        public static CentralPropsUpdateResult ForDryRun(string path, Dictionary<string, string> packageVersions)
            => new(path, packageVersions.Count);
    }

    private sealed record PackagesConfigProject(string ProjectPath, string PackagesConfigPath);
}
