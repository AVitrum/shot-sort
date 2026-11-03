// Creates the key that signs releases, and signs a release's checksum file. Installed copies only accept an update whose
// checksum file carries a signature from this key, so the private key must never reach GitHub or this repository.
//
//   dotnet run installer/release-key.cs -- new [--key <path>]
//   dotnet run installer/release-key.cs -- sign <file.sha256> --public-key <base64> [--key <path>]
//
// The key is an ECDSA P-256 key, encrypted with a passphrase. Keep a copy of the file and the passphrase somewhere safe:
// without them, installed copies can never update by themselves again. SCREENSORTER_KEY_PASSPHRASE replaces the prompt,
// for automated tests with a throwaway key.
using System.Security.Cryptography;
using System.Text;

const string PassphraseVariable = "SCREENSORTER_KEY_PASSPHRASE";
const int MinimumPassphraseLength = 12;

var defaultKey = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".screensorter", "update-signing-key.pem");
return args.FirstOrDefault() switch
{
    "new" => CreateKey(Option("--key") ?? defaultKey),
    "sign" when args.Length > 1 => Sign(args[1], Option("--public-key"), Option("--key") ?? defaultKey),
    _ => Usage(),
};

string? Option(string name)
{
    var at = Array.IndexOf(args, name);
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: release-key.cs new [--key <path>] | sign <file.sha256> --public-key <base64> [--key <path>]");
    return 2;
}

static int CreateKey(string path)
{
    // Replacing a key in use would strand every installed copy.
    if (File.Exists(path))
    {
        Console.Error.WriteLine($"{path} already exists; it is left alone.");
        return 1;
    }

    var passphrase = Environment.GetEnvironmentVariable(PassphraseVariable);
    if (passphrase is null)
    {
        passphrase = Prompt("New passphrase: ");
        if (passphrase != Prompt("Again: "))
        {
            Console.Error.WriteLine("The passphrases differ.");
            return 1;
        }
    }
    if (passphrase.Length < MinimumPassphraseLength)
    {
        Console.Error.WriteLine($"Use a passphrase of at least {MinimumPassphraseLength} characters.");
        return 1;
    }

    using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, key.ExportEncryptedPkcs8PrivateKeyPem(passphrase,
        new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000)));

    Console.WriteLine($"Private key: {path}");
    Console.WriteLine("Back up that file and its passphrase. Never commit or upload them.");
    Console.WriteLine();
    Console.WriteLine("Put this public key in Updates:PublicKey in src/ScreenSorter.App/appsettings.json:");
    Console.WriteLine(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
    return 0;
}

static int Sign(string checksumFile, string? expectedPublicKey, string keyPath)
{
    if (string.IsNullOrWhiteSpace(expectedPublicKey))
    {
        Console.Error.WriteLine("--public-key is required: it stops a release being signed with a key the app does not trust.");
        return 1;
    }
    if (!File.Exists(keyPath))
    {
        Console.Error.WriteLine($"{keyPath} does not exist. Create it with: release-key.cs new");
        return 1;
    }

    using var key = ECDsa.Create();
    try
    {
        key.ImportFromEncryptedPem(File.ReadAllText(keyPath), Environment.GetEnvironmentVariable(PassphraseVariable) ?? Prompt("Passphrase: "));
    }
    catch (Exception ex) when (ex is CryptographicException or ArgumentException)
    {
        Console.Error.WriteLine("The key could not be opened. Is the passphrase right?");
        return 1;
    }
    if (Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()) != expectedPublicKey)
    {
        Console.Error.WriteLine("This key does not match Updates:PublicKey, so installed copies would refuse the release.");
        return 1;
    }

    var signature = key.SignData(File.ReadAllBytes(checksumFile), HashAlgorithmName.SHA256);
    File.WriteAllText(checksumFile + ".sig", Convert.ToBase64String(signature));
    Console.WriteLine($"Signed: {checksumFile}.sig");
    return 0;
}

static string Prompt(string text)
{
    Console.Write(text);
    var typed = new StringBuilder();
    while (Console.ReadKey(intercept: true) is var key && key.Key != ConsoleKey.Enter)
    {
        if (key.Key != ConsoleKey.Backspace)
            typed.Append(key.KeyChar);
        else if (typed.Length > 0)
            typed.Length--;
    }
    Console.WriteLine();
    return typed.ToString();
}
