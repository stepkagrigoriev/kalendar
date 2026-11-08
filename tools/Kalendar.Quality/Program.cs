using System.Diagnostics;
using System.Security.Cryptography;
using System.Xml.Linq;

if (args.Length is < 1 or > 2 || args[0] is not ("coverage" or "security" or "compose") || (args.Length == 2 && args[0] != "coverage"))
{
    Console.Error.WriteLine("Использование: Kalendar.Quality coverage [каталог]|security|compose");
    return 2;
}

if (args[0] == "compose")
{
    var start = new ProcessStartInfo("docker") { UseShellExecute = false };
    foreach (var argument in new[] { "compose", "--env-file", "/dev/null", "config", "--quiet" }) start.ArgumentList.Add(argument);
    foreach (var name in new[] { "MONGO_ROOT_USERNAME", "MONGO_ROOT_PASSWORD", "KALENDAR_WEBAPI_MONGO_USER", "KALENDAR_WEBAPI_MONGO_PASSWORD",
        "KALENDAR_WORKER_MONGO_USER", "KALENDAR_WORKER_MONGO_PASSWORD", "RabbitMq__Username", "RabbitMq__Password", "Jwt__SigningKey", "InternalApi__Key" })
        start.Environment[name] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    start.Environment["Jwt__Issuer"] = "kalendar-ci";
    start.Environment["Jwt__Audience"] = "kalendar-ci";
    start.Environment["Jwt__AccessTokenLifetimeMinutes"] = "30";
    using var process = Process.Start(start)!;
    await process.WaitForExitAsync();
    return process.ExitCode;
}

if (args[0] == "security")
{
    using var git = Process.Start(new ProcessStartInfo("git", "ls-files -z") { RedirectStandardOutput = true, UseShellExecute = false })!;
    var paths = (await git.StandardOutput.ReadToEndAsync()).Split('\0', StringSplitOptions.RemoveEmptyEntries);
    await git.WaitForExitAsync();
    if (git.ExitCode != 0) return git.ExitCode;
    var invalid = new List<string>();
    foreach (var path in paths)
    {
        var filename = Path.GetFileName(path);
        if (filename is ".env" or "ssh.md" or "AGENTS.md" || path.StartsWith("PLANS/", StringComparison.OrdinalIgnoreCase) ||
            filename.EndsWith(".pem", StringComparison.OrdinalIgnoreCase) || filename is "id_ed25519" or "id_rsa")
            invalid.Add(path);
        else if (File.Exists(path))
        {
            var content = await File.ReadAllTextAsync(path);
            if (content.Contains("-----BEGIN " + "OPENSSH PRIVATE KEY-----", StringComparison.Ordinal) ||
                content.Contains("-----BEGIN " + "RSA PRIVATE KEY-----", StringComparison.Ordinal) ||
                content.Contains("-----BEGIN " + "PRIVATE KEY-----", StringComparison.Ordinal))
                invalid.Add(path);
        }
    }
    foreach (var path in invalid) Console.Error.WriteLine($"Запрещённый файл или приватный ключ: {path}");
    return invalid.Count == 0 ? 0 : 1;
}

var required = new[] { "Kalendar.WebApi", "Kalendar.Worker", "Kalendar.NotificationService", "Kalendar.DatabaseSetup" };
var lines = required.ToDictionary(name => name, _ => new Dictionary<string, bool>());
var resultsDirectory = args.Length == 2 ? args[1] : Path.Combine("artifacts", "test-results");
if (!Directory.Exists(resultsDirectory))
{
    Console.Error.WriteLine("Каталог свежих отчётов покрытия не найден.");
    return 1;
}
foreach (var path in Directory.EnumerateFiles(resultsDirectory, "coverage.cobertura.xml", SearchOption.AllDirectories))
{
    var document = XDocument.Load(path);
    foreach (var package in document.Descendants("package"))
    {
        var name = (string?)package.Attribute("name");
        if (name is null || !lines.TryGetValue(name, out var assembly)) continue;
        foreach (var type in package.Descendants("class"))
            foreach (var line in type.Element("lines")?.Elements("line") ?? [])
            {
                var key = $"{type.Attribute("filename")?.Value}:{line.Attribute("number")?.Value}";
                var hit = int.Parse(line.Attribute("hits")!.Value) > 0;
                assembly[key] = assembly.GetValueOrDefault(key) || hit;
            }
    }
}
var failed = false;
foreach (var name in required)
{
    var coverage = lines[name].Count == 0 ? 0 : (double)lines[name].Count(line => line.Value) / lines[name].Count;
    Console.WriteLine($"{name}: {coverage:P2}");
    failed |= coverage <= 0.8;
}
return failed ? 1 : 0;
