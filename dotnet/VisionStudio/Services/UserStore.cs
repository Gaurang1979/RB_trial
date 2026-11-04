using System.Security.Cryptography;
using System.Text.Json;

namespace VisionStudio.Services;

/// <summary>Who may do what. A user can hold several roles.</summary>
public static class Roles
{
    /// <summary>Picks the product, runs the line, performs Golden Sample shift challenge (REQ-12).</summary>
    public const string Operator = "Operator";
    /// <summary>Customer senior supervisor: 4-eyes approvals, recipe assignment, shift logs (REQ-12).</summary>
    public const string Supervisor = "Supervisor";
    /// <summary>Sundaram field service & remote support team: optical calibration, diagnostics, Daily OTP (REQ-17).</summary>
    public const string Service = "Service";
    /// <summary>Sundaram factory manufacturing build & commissioning: hardware specs, feature locks (REQ-17).</summary>
    public const string OEM = "OEM";
    /// <summary>Edits products, camera and reader settings, applies tuning.</summary>
    public const string Engineer = "Engineer";
    /// <summary>Approves locked products and AI models (e-signature), manages users.</summary>
    public const string QA = "QA";

    public static readonly string[] All = { Operator, Supervisor, Service, OEM, Engineer, QA };
}

public sealed class UserRecord
{
    public string Name { get; set; } = "";
    public string FullName { get; set; } = "";
    public List<string> Roles { get; set; } = new();
    public string Salt { get; set; } = "";
    public string Hash { get; set; } = "";
    public bool Disabled { get; set; }
}

/// <summary>
/// Users in data/users.json with PBKDF2-SHA256 password hashes (never plain passwords).
/// The plant's directory (Active Directory) can replace this later.
/// </summary>
public sealed class UserStore
{
    private const int Iterations = 210_000;
    private readonly string _path;
    private readonly object _lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public UserStore(string dataFolder)
    {
        Directory.CreateDirectory(dataFolder);
        _path = Path.Combine(dataFolder, "users.json");
    }

    public bool Any() => Load().Count > 0;

    public IReadOnlyList<UserRecord> List() => Load();

    /// <summary>Checks a password; returns the user, or null (also for disabled users).</summary>
    public UserRecord? Verify(string name, string password)
    {
        var u = Load().FirstOrDefault(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
        if (u is null || u.Disabled) return null;
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(u.Salt), Iterations, HashAlgorithmName.SHA256, 32);
        return CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(u.Hash)) ? u : null;
    }

    public void Save(string name, string fullName, string password, IEnumerable<string> roles)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("user name is required");
        if (password.Length < 8) throw new ArgumentException("password must be at least 8 characters");
        var roleList = roles.Distinct().ToList();
        var bad = roleList.Except(Roles.All).ToList();
        if (bad.Count > 0) throw new ArgumentException("unknown role: " + string.Join(", ", bad));
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        lock (_lock)
        {
            var users = Load();
            users.RemoveAll(x => string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase));
            users.Add(new UserRecord
            {
                Name = name, FullName = fullName, Roles = roleList,
                Salt = Convert.ToBase64String(salt), Hash = Convert.ToBase64String(hash),
            });
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(users, Json));
            File.Move(tmp, _path, overwrite: true);
        }
    }

    private List<UserRecord> Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return new List<UserRecord>();
            return JsonSerializer.Deserialize<List<UserRecord>>(File.ReadAllText(_path)) ?? new List<UserRecord>();
        }
    }
}
