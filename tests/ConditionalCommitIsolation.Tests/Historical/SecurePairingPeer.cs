// Isolated historical-test reconstruction, not original source or current product implementation.
// Reconstructed with ILSpy 11.2.0 from the historical ConditionalCommitIsolation.Tests.dll
// whose SHA-256 is B86FAAC83128F2AD25EA5038DF46C71FB6C4BAA0389E9A9A20177AA5AE35F4F5.
// Only this test project compiles this file. Review changes to product behavior separately.

using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopToolbox.DeskLink;

public sealed class SecurePairingPeer : IAsyncDisposable
{
	private readonly PairingAuthenticatedChannel channel;

	private readonly PairingNetworkHandoff network;

	private readonly CancellationTokenSource lifetime;

	private readonly CancellationToken lifetimeToken;

	private readonly X509Certificate2 certificate;

	private readonly object gate = new object();

	private Task? disposeTask;

	public string LocalFingerprint => channel.LocalFingerprint;

	public string RemoteFingerprint => channel.RemoteFingerprint;

	public CancellationToken LifetimeToken => lifetimeToken;

	public bool BothConfirmed
	{
		get
		{
			if (!lifetime.IsCancellationRequested)
			{
				return channel.Phase == PairingConsentPhase.ReadyToPrepare;
			}
			return false;
		}
	}

	private SecurePairingPeer(PairingAuthenticatedChannel channel, PairingNetworkHandoff network, CancellationTokenSource lifetime, X509Certificate2 certificate)
	{
		this.channel = channel;
		this.network = network;
		this.lifetime = lifetime;
		lifetimeToken = lifetime.Token;
		this.certificate = certificate;
	}

	public static async Task<SecurePairingPeer> AcceptAsync(PairingIncomingRequest request, string keyboardProfile, CancellationToken ct = default(CancellationToken))
	{
		ArgumentNullException.ThrowIfNull(request, "request");
		ct.ThrowIfCancellationRequested();
		PairingIncomingClaim claim = request.TryClaim() ?? throw new InvalidOperationException("This pairing request is no longer available.");
		if (!claim.Binding.HasLiveNetworkOrigin)
		{
			throw new InvalidOperationException("The approved network is unavailable.");
		}
		PairingNetworkHandoff? network = null;
		CancellationTokenSource? lifetime = null;
		X509Certificate2? certificate = null;
		Socket? socket = null;
		PairingAuthenticatedChannel? channel = null;
		try
		{
			using (CancellationTokenSource handoff = CancellationTokenSource.CreateLinkedTokenSource(ct, claim.RevocationToken))
			{
				network = await claim.AcquireNetworkAsync(handoff.Token).ConfigureAwait(continueOnCapturedContext: false);
			}
			ct.ThrowIfCancellationRequested();
			if (network == null || !network.Binding.HasLiveNetworkOrigin || network.RevocationToken.IsCancellationRequested ||
				!(await network.RevalidateAsync(ct).ConfigureAwait(continueOnCapturedContext: false)))
			{
				throw new InvalidOperationException("The approved network changed.");
			}
			PairingDiscoveryNotice notice = claim.Notice;
			DateTimeOffset utcNow = DateTimeOffset.UtcNow;
			if (utcNow >= claim.ExpiresAtUtc || utcNow >= notice.SessionExpiresAtUtc || notice.SessionExpiresAtUtc > claim.Offer.ExpiresAtUtc || notice.SessionExpiresAtUtc > utcNow.AddMinutes(2.0))
			{
				throw new InvalidOperationException("The pairing request expired.");
			}
			lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, network.RevocationToken);
			lifetime.CancelAfter(notice.SessionExpiresAtUtc - utcNow);
			string path = CertificatePath(keyboardProfile);
			if (!File.Exists(path))
			{
				throw new InvalidOperationException("The local keyboard identity is unavailable.");
			}
			certificate = PrivateFiles.LoadTlsCertificate(path);
			if (!certificate.HasPrivateKey || !DeskLinkFingerprint.Matches(claim.Offer.KeyboardFingerprint, DeskLinkFingerprint.OfCertificate(certificate)))
			{
				throw new InvalidOperationException("The selected local identity changed.");
			}
			if (!(await network.RevalidateAsync(lifetime.Token).ConfigureAwait(continueOnCapturedContext: false)))
			{
				throw new InvalidOperationException("The approved network changed.");
			}
			socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
			{
				ExclusiveAddressUse = true
			};
			DiscoveryInterfacePin.Configure(socket, network.Binding.InterfaceIndex);
			socket.Bind(new IPEndPoint(network.Binding.Address, 0));
			await socket.ConnectAsync(new IPEndPoint(claim.SourceAddress, notice.Port), lifetime.Token).ConfigureAwait(continueOnCapturedContext: false);
			bool flag3 = !(socket.LocalEndPoint is IPEndPoint iPEndPoint) || !iPEndPoint.Address.Equals(network.Binding.Address) || !(socket.RemoteEndPoint is IPEndPoint iPEndPoint2) || !iPEndPoint2.Address.Equals(claim.SourceAddress) || iPEndPoint2.Port != notice.Port;
			bool flag4 = flag3;
			if (!flag4)
			{
				flag4 = !(await network.RevalidateAsync(lifetime.Token).ConfigureAwait(continueOnCapturedContext: false));
			}
			if (flag4)
			{
				throw new InvalidOperationException("The pairing connection is not on the approved network.");
			}
			PairingSessionIdentity identity = new PairingSessionIdentity(notice.SessionId, notice.OfferNonce, notice.TransactionId, notice.HostName, notice.PeerName, notice.HostFingerprint, claim.Offer.KeyboardFingerprint, notice.SessionExpiresAtUtc);
			channel = await PairingAuthenticatedChannel.StartPeerAsync(socket, certificate, identity, claim.Offer, lifetime.Token).ConfigureAwait(continueOnCapturedContext: false);
			socket = null;
			bool isCancellationRequested = lifetime.IsCancellationRequested;
			bool flag5 = isCancellationRequested;
			if (!flag5)
			{
				flag5 = !(await network.RevalidateAsync(lifetime.Token).ConfigureAwait(continueOnCapturedContext: false));
			}
			if (flag5)
			{
				throw new InvalidOperationException("The approved network changed.");
			}
			SecurePairingPeer result = new SecurePairingPeer(channel, network, lifetime, certificate);
			channel = null;
			network = null;
			lifetime = null;
			certificate = null;
			return result;
		}
		catch
		{
			lifetime?.Cancel();
			socket?.Dispose();
			if (channel != null)
			{
				await channel.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			certificate?.Dispose();
			if (network != null)
			{
				await network.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			lifetime?.Dispose();
			throw;
		}
	}

	public async Task ConfirmAsync(CancellationToken ct = default(CancellationToken))
	{
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
		_ = 1;
		try
		{
			if (!(await network.RevalidateAsync(linked.Token).ConfigureAwait(continueOnCapturedContext: false)))
			{
				throw new InvalidOperationException("The approved network changed.");
			}
			await channel.ConfirmLocalAsync(linked.Token).ConfigureAwait(continueOnCapturedContext: false);
		}
		catch
		{
			CancelLifetime();
			throw;
		}
	}

	public async Task WaitForRemoteConfirmationAsync(CancellationToken ct = default(CancellationToken))
	{
		using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
		_ = 1;
		try
		{
			await channel.ReceiveRemoteConfirmationAsync(linked.Token).ConfigureAwait(continueOnCapturedContext: false);
			if (!(await network.RevalidateAsync(linked.Token).ConfigureAwait(continueOnCapturedContext: false)))
			{
				throw new InvalidOperationException("The approved network changed.");
			}
		}
		catch
		{
			CancelLifetime();
			throw;
		}
	}

	private void CancelLifetime()
	{
		try
		{
			lifetime.Cancel();
		}
		catch (ObjectDisposedException)
		{
		}
	}

	public ValueTask DisposeAsync()
	{
		lock (gate)
		{
			return new ValueTask(disposeTask ?? (disposeTask = DisposeCoreAsync()));
		}
	}

	private async Task DisposeCoreAsync()
	{
		try
		{
			lifetime.Cancel();
		}
		finally
		{
			try
			{
				await channel.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
			}
			finally
			{
				try
				{
					await network.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
				}
				finally
				{
					certificate.Dispose();
					lifetime.Dispose();
				}
			}
		}
	}

	private static string CertificatePath(string keyboardProfile)
	{
		if (string.IsNullOrWhiteSpace(keyboardProfile) || !Path.IsPathFullyQualified(keyboardProfile))
		{
			throw new ArgumentException("An absolute keyboard profile is required.", "keyboardProfile");
		}
		string text = Path.Combine(Path.GetFullPath(keyboardProfile), "tls", "deskflow.pem");
		for (string? text2 = text; text2 != null; text2 = Path.GetDirectoryName(text2))
		{
			try
			{
				if ((File.GetAttributes(text2) & FileAttributes.ReparsePoint) != 0)
				{
					throw new IOException("Linked identity path is not allowed.");
				}
			}
			catch (FileNotFoundException)
			{
			}
			catch (DirectoryNotFoundException)
			{
			}
		}
		return text;
	}
}
