using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;

namespace Pishkhan.Web.Auth;

/// <summary>Configuration of one token audience (users or staff). Each has its own issuer, audience and ES256 key.</summary>
public sealed class JwtAudienceOptions
{
    public string Issuer { get; set; } = "https://api.pishkhan.ir";

    public string Audience { get; set; } = "pishkhan-api";

    /// <summary>PKCS#8 PEM of the active P-256 private key. Required outside Development and Testing.</summary>
    public string? SigningKeyPem { get; set; }

    public string KeyId { get; set; } = "k1";

    /// <summary>PEMs of retired public keys still accepted until their tokens expire (key rotation).</summary>
    public List<string> PreviousPublicKeysPem { get; set; } = [];

    public TimeSpan AccessTokenLifetime { get; set; } = TimeSpan.FromMinutes(10);
}

/// <summary>The signing key and validation keys of one audience.</summary>
public sealed class JwtKeyRing
{
    public JwtKeyRing(string name, JwtAudienceOptions options, IHostEnvironment environment, IConfiguration configuration)
    {
        Name = name;
        Options = options;
        var ecdsa = ECDsa.Create();
        if (options.SigningKeyPem is { Length: > 0 } pem)
        {
            ecdsa.ImportFromPem(pem);
        }
        else if (environment.IsDevelopment() || environment.IsEnvironment("Testing"))
        {
            ecdsa.ImportFromPem(DevKey(name, configuration));
        }
        else
        {
            throw new InvalidOperationException($"Auth:{name}:SigningKeyPem must be configured.");
        }

        SigningKey = new ECDsaSecurityKey(ecdsa) { KeyId = options.KeyId };
        SigningCredentials = new SigningCredentials(SigningKey, SecurityAlgorithms.EcdsaSha256);
        var validation = new List<SecurityKey> { SigningKey };
        foreach (var previous in options.PreviousPublicKeysPem)
        {
            var key = ECDsa.Create();
            key.ImportFromPem(previous);
            validation.Add(new ECDsaSecurityKey(key));
        }

        ValidationKeys = validation;
    }

    public string Name { get; }

    public JwtAudienceOptions Options { get; }

    public ECDsaSecurityKey SigningKey { get; }

    public SigningCredentials SigningCredentials { get; }

    public IReadOnlyList<SecurityKey> ValidationKeys { get; }

    /// <summary>Dev keys live outside the repository and are shared by all local hosts.</summary>
    private static string DevKey(string name, IConfiguration configuration)
    {
        var directory = configuration["Auth:DevKeyDirectory"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pishkhan", "keys");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"jwt-{name}.pem");
        if (!File.Exists(path))
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                File.WriteAllText(path, key.ExportPkcs8PrivateKeyPem());
            }
            catch (IOException) when (File.Exists(path))
            {
                // created concurrently by another host
            }
        }

        return File.ReadAllText(path);
    }

    public object ToJwk()
    {
        var parameters = SigningKey.ECDsa.ExportParameters(false);
        return new
        {
            kty = "EC",
            crv = "P-256",
            use = "sig",
            alg = "ES256",
            kid = SigningKey.KeyId,
            x = Base64UrlEncoder.Encode(parameters.Q.X),
            y = Base64UrlEncoder.Encode(parameters.Q.Y),
        };
    }
}
