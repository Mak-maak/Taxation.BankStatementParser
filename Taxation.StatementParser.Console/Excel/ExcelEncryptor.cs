using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using OpenMcdf;

namespace Taxation.StatementParser.Console.Excel;

/// <summary>
/// Applies a genuine "password to open" to an OOXML (.xlsx) file using ECMA-376 Agile Encryption
/// (AES-256-CBC + SHA-512), as defined by [MS-OFFCRYPTO]. The encrypted package is stored inside an
/// OLE Compound File, which is what Excel expects for a password-protected workbook.
/// </summary>
/// <remarks>
/// This is real file-level encryption: without the password the contents cannot be read. It is not
/// the same as ClosedXML's structural workbook/worksheet protection, which leaves the data readable.
/// </remarks>
internal static class ExcelEncryptor
{
    private const int KeyBits = 256;
    private const int KeyBytes = KeyBits / 8;
    private const int BlockBytes = 16;
    private const int SaltBytes = 16;
    private const int SpinCount = 100_000;
    private const int SegmentSize = 4096;

    private static readonly XNamespace EncNs = "http://schemas.microsoft.com/office/2006/encryption";
    private static readonly XNamespace PasswordNs = "http://schemas.microsoft.com/office/2006/keyEncryptor/password";

    // Block keys defined by [MS-OFFCRYPTO] for deriving the various keys/hashes.
    private static readonly byte[] BlockKeyVerifierInput = [0xFE, 0xA7, 0xD2, 0x76, 0x3B, 0x4B, 0x9E, 0x79];
    private static readonly byte[] BlockKeyVerifierValue = [0xD7, 0xAA, 0x0F, 0x6D, 0x30, 0x61, 0x34, 0x4E];
    private static readonly byte[] BlockKeyEncryptedKey = [0x14, 0x6E, 0x0B, 0xE7, 0xAB, 0xAC, 0xD0, 0xD6];
    private static readonly byte[] IntegrityKeyBlock = [0x5F, 0xB2, 0xAD, 0x01, 0x0C, 0xB9, 0xE1, 0xF6];
    private static readonly byte[] IntegrityValueBlock = [0xA0, 0x67, 0x7F, 0x02, 0xB2, 0x2C, 0x84, 0x33];

    /// <summary>
    /// Encrypts the file at <paramref name="filePath"/> in place so it can only be opened with
    /// <paramref name="password"/>.
    /// </summary>
    public static void Encrypt(string filePath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(password);

        byte[] plainPackage = File.ReadAllBytes(filePath);

        byte[] keySalt = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] keyDataSalt = RandomNumberGenerator.GetBytes(SaltBytes);

        // The intermediate key that actually encrypts the package payload.
        byte[] packageKey = RandomNumberGenerator.GetBytes(KeyBytes);

        // Derive the password-based key that wraps the package key.
        byte[] passwordHash = HashPassword(password, keySalt);
        byte[] keyEncryptionKey = DeriveKey(passwordHash, BlockKeyEncryptedKey);
        byte[] encryptedKeyValue = AesCbcNoPadding(packageKey, keyEncryptionKey, keySalt, encrypt: true);

        // Password verifier: a random value and its hash, both encrypted with password-derived keys.
        byte[] verifierInput = RandomNumberGenerator.GetBytes(SaltBytes);
        byte[] verifierInputKey = DeriveKey(passwordHash, BlockKeyVerifierInput);
        byte[] encryptedVerifierInput = AesCbcNoPadding(verifierInput, verifierInputKey, keySalt, encrypt: true);

        byte[] verifierHash = SHA512.HashData(verifierInput);
        byte[] verifierHashKey = DeriveKey(passwordHash, BlockKeyVerifierValue);
        byte[] encryptedVerifierHash = AesCbcNoPadding(PadTo(verifierHash, BlockBytes), verifierHashKey, keySalt, encrypt: true);

        // Encrypt the package in 4096-byte segments, each with its own IV.
        byte[] encryptedPackage = EncryptPackage(plainPackage, packageKey, keyDataSalt);

        // HMAC integrity over the encrypted package.
        (byte[] encryptedHmacKey, byte[] encryptedHmacValue) = ComputeIntegrity(encryptedPackage, packageKey, keyDataSalt);

        byte[] encryptionInfo = BuildEncryptionInfo(
            keyDataSalt,
            keySalt,
            encryptedVerifierInput,
            encryptedVerifierHash,
            encryptedKeyValue,
            encryptedHmacKey,
            encryptedHmacValue);

        WriteCompoundFile(filePath, encryptionInfo, encryptedPackage, plainPackage.LongLength);
    }

    private static byte[] EncryptPackage(byte[] plainPackage, byte[] packageKey, byte[] keyDataSalt)
    {
        // The stream is prefixed with the little-endian 8-byte length of the plaintext package.
        using var output = new MemoryStream();
        Span<byte> lengthPrefix = stackalloc byte[8];
        BitConverter.TryWriteBytes(lengthPrefix, (ulong)plainPackage.LongLength);
        output.Write(lengthPrefix);

        int segmentIndex = 0;
        for (int offset = 0; offset < plainPackage.Length; offset += SegmentSize)
        {
            int count = Math.Min(SegmentSize, plainPackage.Length - offset);
            byte[] block = new byte[count];
            Array.Copy(plainPackage, offset, block, 0, count);

            byte[] iv = SegmentIv(keyDataSalt, segmentIndex);
            byte[] padded = PadTo(block, BlockBytes);
            byte[] encrypted = AesCbcNoPadding(padded, packageKey, iv, encrypt: true);
            output.Write(encrypted);
            segmentIndex++;
        }

        return output.ToArray();
    }

    private static (byte[] EncryptedKey, byte[] EncryptedValue) ComputeIntegrity(
        byte[] encryptedPackage,
        byte[] packageKey,
        byte[] keyDataSalt)
    {
        byte[] hmacKey = RandomNumberGenerator.GetBytes(64);

        byte[] hmacKeyIv = DeriveIv(keyDataSalt, IntegrityKeyBlock);
        byte[] encryptedHmacKey = AesCbcNoPadding(PadTo(hmacKey, BlockBytes), packageKey, hmacKeyIv, encrypt: true);

        byte[] hmacValue;
        using (var hmac = new HMACSHA512(hmacKey))
        {
            hmacValue = hmac.ComputeHash(encryptedPackage);
        }

        byte[] hmacValueIv = DeriveIv(keyDataSalt, IntegrityValueBlock);
        byte[] encryptedHmacValue = AesCbcNoPadding(PadTo(hmacValue, BlockBytes), packageKey, hmacValueIv, encrypt: true);

        return (encryptedHmacKey, encryptedHmacValue);
    }

    private static byte[] BuildEncryptionInfo(
        byte[] keyDataSalt,
        byte[] keySalt,
        byte[] encryptedVerifierInput,
        byte[] encryptedVerifierHash,
        byte[] encryptedKeyValue,
        byte[] encryptedHmacKey,
        byte[] encryptedHmacValue)
    {
        var keyData = new XElement(EncNs + "keyData",
            new XAttribute("saltSize", SaltBytes),
            new XAttribute("blockSize", BlockBytes),
            new XAttribute("keyBits", KeyBits),
            new XAttribute("hashSize", 64),
            new XAttribute("cipherAlgorithm", "AES"),
            new XAttribute("cipherChaining", "ChainingModeCBC"),
            new XAttribute("hashAlgorithm", "SHA512"),
            new XAttribute("saltValue", Convert.ToBase64String(keyDataSalt)));

        var dataIntegrity = new XElement(EncNs + "dataIntegrity",
            new XAttribute("encryptedHmacKey", Convert.ToBase64String(encryptedHmacKey)),
            new XAttribute("encryptedHmacValue", Convert.ToBase64String(encryptedHmacValue)));

        var encryptedKey = new XElement(PasswordNs + "encryptedKey",
            new XAttribute("spinCount", SpinCount),
            new XAttribute("saltSize", SaltBytes),
            new XAttribute("blockSize", BlockBytes),
            new XAttribute("keyBits", KeyBits),
            new XAttribute("hashSize", 64),
            new XAttribute("cipherAlgorithm", "AES"),
            new XAttribute("cipherChaining", "ChainingModeCBC"),
            new XAttribute("hashAlgorithm", "SHA512"),
            new XAttribute("saltValue", Convert.ToBase64String(keySalt)),
            new XAttribute("encryptedVerifierHashInput", Convert.ToBase64String(encryptedVerifierInput)),
            new XAttribute("encryptedVerifierHashValue", Convert.ToBase64String(encryptedVerifierHash)),
            new XAttribute("encryptedKeyValue", Convert.ToBase64String(encryptedKeyValue)));

        var keyEncryptors = new XElement(EncNs + "keyEncryptors",
            new XElement(EncNs + "keyEncryptor",
                new XAttribute("uri", "http://schemas.microsoft.com/office/2006/keyEncryptor/password"),
                encryptedKey));

        var encryption = new XElement(EncNs + "encryption",
            new XAttribute(XNamespace.Xmlns + "p", PasswordNs.NamespaceName),
            keyData,
            dataIntegrity,
            keyEncryptors);

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), encryption);

        using var xmlStream = new MemoryStream();
        using (var writer = new StreamWriter(xmlStream, new UTF8Encoding(false)))
        {
            writer.Write(doc.Declaration + "\r\n");
            writer.Write(encryption.ToString(SaveOptions.DisableFormatting));
            writer.Flush();
        }

        byte[] xml = xmlStream.ToArray();

        using var result = new MemoryStream();
        // EncryptionInfo header: version 4.4 (Agile) with reserved flags 0x40.
        result.Write(BitConverter.GetBytes((ushort)4));
        result.Write(BitConverter.GetBytes((ushort)4));
        result.Write(BitConverter.GetBytes(0x40));
        result.Write(xml);
        return result.ToArray();
    }

    private static void WriteCompoundFile(string filePath, byte[] encryptionInfo, byte[] encryptedPackage, long plainLength)
    {
        _ = plainLength;
        // Rewrite the target file as an OLE Compound File containing the two required streams.
        using var root = RootStorage.Create(filePath);

        using (CfbStream infoStream = root.CreateStream("EncryptionInfo"))
        {
            infoStream.Write(encryptionInfo, 0, encryptionInfo.Length);
        }

        using (CfbStream packageStream = root.CreateStream("EncryptedPackage"))
        {
            packageStream.Write(encryptedPackage, 0, encryptedPackage.Length);
        }

        root.Flush(consolidate: true);
    }

    private static byte[] HashPassword(string password, byte[] salt)
    {
        byte[] passwordBytes = Encoding.Unicode.GetBytes(password);

        byte[] hash = SHA512.HashData(Concat(salt, passwordBytes));
        Span<byte> iterationBuffer = stackalloc byte[4 + 64];
        for (int i = 0; i < SpinCount; i++)
        {
            BitConverter.TryWriteBytes(iterationBuffer, i);
            hash.CopyTo(iterationBuffer[4..]);
            hash = SHA512.HashData(iterationBuffer);
        }

        return hash;
    }

    private static byte[] DeriveKey(byte[] passwordHash, byte[] blockKey)
    {
        byte[] combined = SHA512.HashData(Concat(passwordHash, blockKey));
        return Fit(combined, KeyBytes, 0x36);
    }

    private static byte[] SegmentIv(byte[] keyDataSalt, int segmentIndex)
    {
        byte[] blockKey = BitConverter.GetBytes(segmentIndex);
        return DeriveIv(keyDataSalt, blockKey);
    }

    private static byte[] DeriveIv(byte[] keyDataSalt, byte[] blockKey)
    {
        byte[] hash = SHA512.HashData(Concat(keyDataSalt, blockKey));
        return Fit(hash, BlockBytes, 0x36);
    }

    private static byte[] AesCbcNoPadding(byte[] data, byte[] key, byte[] iv, bool encrypt)
    {
        using var aes = Aes.Create();
        aes.KeySize = KeyBits;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = Fit(iv, BlockBytes, 0x00);

        using ICryptoTransform transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
        return transform.TransformFinalBlock(data, 0, data.Length);
    }

    private static byte[] PadTo(byte[] data, int blockSize)
    {
        int remainder = data.Length % blockSize;
        if (remainder == 0)
        {
            return data;
        }

        byte[] padded = new byte[data.Length + (blockSize - remainder)];
        Array.Copy(data, padded, data.Length);
        return padded;
    }

    private static byte[] Fit(byte[] source, int size, byte fill)
    {
        byte[] result = new byte[size];
        for (int i = 0; i < size; i++)
        {
            result[i] = i < source.Length ? source[i] : fill;
        }

        return result;
    }

    private static byte[] Concat(byte[] first, byte[] second)
    {
        byte[] result = new byte[first.Length + second.Length];
        Buffer.BlockCopy(first, 0, result, 0, first.Length);
        Buffer.BlockCopy(second, 0, result, first.Length, second.Length);
        return result;
    }
}
