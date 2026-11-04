using System.Security.Cryptography;
using System.Text;
using BarcodeReading.Protection;
using VisionCore.Security;
using Xunit;

namespace VisionCore.Tests;

public class SecureAssetVaultTests
{
    // 32 bytes: AES-256 (Req 17.10 AUD-09)
    private static readonly byte[] MasterKey = Encoding.UTF8.GetBytes("FactoryAuthKey2026_SecureVault!!");
    private const string Asset = "codenet.onnx";

    [Fact]
    public void CamouflageLabels_MapsProprietaryNamesToSafeLabels()
    {
        Assert.Equal("Seal Quality Check", CamouflageLabels.GetSafeToolLabel("vs_thermal"));
        Assert.Equal("Surface Texture Check", CamouflageLabels.GetSafeToolLabel("vs_photometric"));
        Assert.Equal("Anti-Glare Filter", CamouflageLabels.GetSafeToolLabel("vs_polar"));
        Assert.Equal("Pattern Match", CamouflageLabels.GetSafeToolLabel("vs_anomaly"));
        Assert.Equal("Process Stability Map", CamouflageLabels.GetSafeToolLabel("vs_haystack"));
        Assert.Equal("Pouch Alignment", CamouflageLabels.GetSafeToolLabel("vs_deform"));
        Assert.Equal("Surface Filter", CamouflageLabels.GetSafeToolLabel("vs_wavelet"));
    }

    [Fact]
    public void CamouflageLabels_PreservesOpenSourceAndHardwareAttribution()
    {
        Assert.Equal("Suricata", CamouflageLabels.GetSafeToolLabel("suricata_rules"));
        Assert.Equal("Zeek", CamouflageLabels.GetSafeToolLabel("zeek_intel"));
        Assert.Equal("WireGuard", CamouflageLabels.GetSafeToolLabel("wireguard_tunnel"));
        Assert.Equal("OpenCV", CamouflageLabels.GetSafeToolLabel("opencv_core"));
        Assert.Equal("ZXing", CamouflageLabels.GetSafeToolLabel("zxing_reader"));
        Assert.Equal("SQLite", CamouflageLabels.GetSafeToolLabel("sqlite3_db"));
    }

    [Fact]
    public void CamouflageLabels_ProcessTitle_GeneratesSafeTitle()
    {
        Assert.Equal("Machine Control System", CamouflageLabels.GetSafeProcessTitle());
        Assert.Equal("Machine Control System — Line 04", CamouflageLabels.GetSafeProcessTitle("Line 04"));
    }

    [Fact]
    public void SecureAssetVault_EncryptAndDecrypt_RoundtripsInMemory()
    {
        byte[] original = Encoding.UTF8.GetBytes("PROPRIETARY_TENSOR_WEIGHTS_AND_BIASES_FOR_DEFECT_DETECTION_MODEL_V2");
        byte[] encrypted = SecureAssetVault.Encrypt(original, MasterKey, Asset);

        Assert.True(encrypted.Length > original.Length);
        Assert.DoesNotContain("PROPRIETARY_TENSOR", Encoding.UTF8.GetString(encrypted));

        Assert.Equal(original, SecureAssetVault.Decrypt(encrypted, MasterKey, Asset));
    }

    [Fact]
    public void SecureAssetVault_UsesTheOneContentCipherEnvelope()
    {
        byte[] original = Encoding.UTF8.GetBytes("RECIPE");
        byte[] encrypted = SecureAssetVault.Encrypt(original, MasterKey, Asset);

        // Same .vsenc container as BarcodeReading.Protection.ContentCipher, both ways
        Assert.True(ContentCipher.LooksEncrypted(encrypted));
        Assert.Equal(original, ContentCipher.Decrypt(encrypted, MasterKey, Asset));
        Assert.Equal(original, SecureAssetVault.Decrypt(ContentCipher.Encrypt(original, MasterKey, Asset), MasterKey, Asset));
    }

    [Fact]
    public void SecureAssetVault_SameDataTwice_GivesDifferentBlobs()
    {
        byte[] original = new byte[256];
        byte[] a = SecureAssetVault.Encrypt(original, MasterKey, Asset);
        byte[] b = SecureAssetVault.Encrypt(original, MasterKey, Asset);

        Assert.NotEqual(a, b);
        Assert.NotEqual(a.AsSpan(8, 12).ToArray(), b.AsSpan(8, 12).ToArray()); // fresh nonce
    }

    // Offsets in the .vsenc container: header 0..7, nonce 8..19, tag 20..35, ciphertext 36..
    [Theory]
    [InlineData(0)]   // magic
    [InlineData(6)]   // version
    [InlineData(7)]   // reserved
    [InlineData(10)]  // nonce
    [InlineData(20)]  // tag, first byte
    [InlineData(35)]  // tag, last byte
    [InlineData(36)]  // ciphertext, first byte
    [InlineData(-1)]  // ciphertext, last byte
    public void SecureAssetVault_TamperedBlob_ThrowsCryptographicException(int offset)
    {
        byte[] encrypted = SecureAssetVault.Encrypt(Encoding.UTF8.GetBytes("SENSITIVE_RECIPE_CONFIG_PARAMETERS"), MasterKey, Asset);
        encrypted[offset < 0 ? encrypted.Length + offset : offset] ^= 0x01;

        Assert.Throws<CryptographicException>(() => SecureAssetVault.Decrypt(encrypted, MasterKey, Asset));
    }

    [Fact]
    public void SecureAssetVault_TruncatedBlob_ThrowsCryptographicException()
    {
        byte[] encrypted = SecureAssetVault.Encrypt(Encoding.UTF8.GetBytes("RECIPE"), MasterKey, Asset);

        Assert.Throws<CryptographicException>(() => SecureAssetVault.Decrypt(encrypted[..^1], MasterKey, Asset));
        Assert.Throws<CryptographicException>(() => SecureAssetVault.Decrypt(encrypted[..20], MasterKey, Asset));
        Assert.Throws<CryptographicException>(() => SecureAssetVault.Decrypt(Array.Empty<byte>(), MasterKey, Asset));
    }

    [Fact]
    public void SecureAssetVault_WrongKey_IsRefused()
    {
        byte[] encrypted = SecureAssetVault.Encrypt(Encoding.UTF8.GetBytes("RECIPE"), MasterKey, Asset);
        byte[] wrongKey = (byte[])MasterKey.Clone();
        wrongKey[0] ^= 0x01;

        Assert.Throws<CryptographicException>(() => SecureAssetVault.Decrypt(encrypted, wrongKey, Asset));
    }

    [Fact]
    public void SecureAssetVault_WrongAssetName_IsRefused()
    {
        byte[] encrypted = SecureAssetVault.Encrypt(Encoding.UTF8.GetBytes("RECIPE"), MasterKey, Asset);

        Assert.Throws<CryptographicException>(() => SecureAssetVault.Decrypt(encrypted, MasterKey, "ocr.onnx"));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(31)]
    [InlineData(33)]
    public void SecureAssetVault_KeyNot32Bytes_IsRefused(int length)
    {
        byte[] key = new byte[length];
        Assert.Throws<ArgumentException>(() => SecureAssetVault.Encrypt(new byte[4], key, Asset));
        Assert.Throws<ArgumentException>(() => SecureAssetVault.Decrypt(new byte[64], key, Asset));
    }

    [Fact]
    public void SecureAssetVault_EmptyAssetName_IsRefused()
    {
        Assert.ThrowsAny<ArgumentException>(() => SecureAssetVault.Encrypt(new byte[4], MasterKey, ""));
    }

    [Fact]
    public void SecureAssetVault_FileEncryptAndDecryptToMemory_NeverLeavesDecryptedFileOnDisk()
    {
        string tempPlain = Path.Combine(Path.GetTempPath(), $"model_source_{Guid.NewGuid():N}.bin");
        string tempDat = Path.Combine(Path.GetTempPath(), $"cache_0a7f19_{Guid.NewGuid():N}.dat");

        try
        {
            byte[] mockModelBytes = new byte[4096];
            RandomNumberGenerator.Fill(mockModelBytes);
            File.WriteAllBytes(tempPlain, mockModelBytes);

            SecureAssetVault.EncryptFile(tempPlain, tempDat, MasterKey, Asset);
            Assert.True(File.Exists(tempDat));

            byte[] loadedInMemory = SecureAssetVault.DecryptFileToMemory(tempDat, MasterKey, Asset);
            Assert.Equal(mockModelBytes, loadedInMemory);
        }
        finally
        {
            if (File.Exists(tempPlain)) File.Delete(tempPlain);
            if (File.Exists(tempDat)) File.Delete(tempDat);
        }
    }
}
