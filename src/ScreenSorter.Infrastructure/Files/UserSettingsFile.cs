using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Configuration.Memory;
using ScreenSorter.Core.Configuration;
using ScreenSorter.Core.Settings;

namespace ScreenSorter.Infrastructure.Files;

/// <summary>Reads and writes the file the settings window saves to.</summary>
public static class UserSettingsFile
{
    private const string BaseFileName = "appsettings.json";

    /// <summary>
    /// Replaces the <c>appsettings.json</c> source of a host's configuration with that file merged with the user's
    /// settings (<see cref="SettingsMerger"/>). The source keeps its position, so command-line values still win.
    /// Does nothing if there is no user file.
    /// </summary>
    /// <param name="configuration">The host's configuration; the file path comes from <see cref="PathsOptions.UserSettingsPath"/>.</param>
    /// <param name="baseDirectory">Folder holding <c>appsettings.json</c>.</param>
    /// <returns>A message if the user's file could not be used and was ignored; otherwise <see langword="null"/>.</returns>
    public static string? Apply(IConfigurationBuilder configuration, string baseDirectory)
    {
        var current = configuration.Build();
        var path = PathsOptions.Expand(current[$"{PathsOptions.Section}:{nameof(PathsOptions.UserSettingsPath)}"] ?? "");
        if (path.Length == 0 || !File.Exists(path))
            return null;

        var index = -1;
        for (var i = 0; i < configuration.Sources.Count; i++)
        {
            if (configuration.Sources[i] is JsonConfigurationSource { Path: BaseFileName })
                index = i;
        }
        if (index < 0)
            return $"{BaseFileName} is not among the configuration sources";

        try
        {
            var merged = SettingsMerger.Merge(File.ReadAllText(Path.Combine(baseDirectory, BaseFileName)), File.ReadAllText(path));
            // A stream source cannot be loaded twice and the host reloads its sources, so the merged values are kept in memory.
            var values = new ConfigurationBuilder()
                .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(merged)))
                .Build()
                .AsEnumerable()
                .ToList();
            configuration.Sources[index] = new MemoryConfigurationSource { InitialData = values };
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FormatException or InvalidDataException)
        {
            return $"Ignored {path}: {ex.Message}";
        }
    }

    /// <summary>Saves settings, replacing the file only once the new content is fully written.</summary>
    /// <param name="configuration">The app's configuration, for the file path.</param>
    /// <param name="json">The file's new content.</param>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">The file could not be written.</exception>
    public static void Save(PathsOptions configuration, string json)
    {
        var path = PathsOptions.Expand(configuration.UserSettingsPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temporary, path, overwrite: true);
    }
}
