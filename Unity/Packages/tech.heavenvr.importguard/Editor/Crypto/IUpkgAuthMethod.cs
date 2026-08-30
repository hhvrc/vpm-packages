using System;
using System.IO;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// One way to gate an encrypted package (password today; a keyfile or something
    /// org-managed later). UpkgCrypto's container only stores each method's
    /// <see cref="TypeId"/> in the header, never which one was used to write a given
    /// file beyond that byte, so a file written with today's methods still opens with
    /// a future build, and a file that needs a method this build doesn't have fails
    /// with a clear "needs a newer Import Guard" error instead of silently misreading.
    ///
    /// Credentials travel as <c>object</c> because different methods need different
    /// shapes (a password is a string; a keyfile method might need bytes) - only the
    /// method itself and whatever prompted for the credential need agree on the type.
    /// </summary>
    public interface IUpkgAuthMethod
    {
        byte TypeId { get; }
        string DisplayName { get; }

        /// <summary>
        /// Writes this method's own header fields (e.g. a KDF salt) to
        /// <paramref name="header"/> and returns the key material derived from
        /// <paramref name="credential"/>, ready to encrypt with immediately.
        /// <paramref name="onProgress"/>, if given, is called with 0..1 as
        /// derivation proceeds - key derivation is deliberately slow (brute-force
        /// resistance), so a caller driving a progress bar off it needs real
        /// incremental values, not one jump from 0 to 1.
        /// </summary>
        byte[] WriteHeaderAndDeriveKey(BinaryWriter header, object credential, Action<float> onProgress);

        /// <summary>
        /// Reads back the fields <see cref="WriteHeaderAndDeriveKey"/> wrote and
        /// re-derives the same key material from a fresh <paramref name="credential"/>
        /// of the same shape. A wrong credential is expected to still return
        /// deterministic (just wrong) key material rather than throw - UpkgCrypto
        /// tells the two apart via its own integrity check, not this call.
        /// </summary>
        byte[] ReadHeaderAndDeriveKey(BinaryReader header, object credential, Action<float> onProgress);

        /// <summary>Editor UI: asks the user to set a credential for a new export.
        /// Returns null if the user cancelled.</summary>
        object PromptForNewCredential(string packageName);

        /// <summary>Editor UI: asks the user for the credential to open an existing
        /// encrypted package. Returns null if the user cancelled.</summary>
        object PromptForExistingCredential(string packageName);
    }
}
