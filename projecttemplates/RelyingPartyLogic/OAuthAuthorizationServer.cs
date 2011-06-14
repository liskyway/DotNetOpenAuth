﻿//-----------------------------------------------------------------------
// <copyright file="OAuthAuthorizationServer.cs" company="Andrew Arnott">
//     Copyright (c) Andrew Arnott. All rights reserved.
// </copyright>
//-----------------------------------------------------------------------

namespace RelyingPartyLogic {
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Security.Cryptography;
	using System.Security.Cryptography.X509Certificates;
	using System.Text;
	using System.Web;
	using DotNetOpenAuth.Messaging.Bindings;
	using DotNetOpenAuth.OAuth2;
	using DotNetOpenAuth.OAuth2.ChannelElements;
	using DotNetOpenAuth.OAuth2.Messages;

	/// <summary>
	/// Provides OAuth 2.0 authorization server information to DotNetOpenAuth.
	/// </summary>
	public class OAuthAuthorizationServer : IAuthorizationServer {
		private static readonly RSAParameters AsymmetricKey = CreateRSAKey();

		private readonly INonceStore nonceStore = new NonceDbStore();

		/// <summary>
		/// Initializes a new instance of the <see cref="OAuthAuthorizationServer"/> class.
		/// </summary>
		public OAuthAuthorizationServer() {
			this.CryptoKeyStore = new RelyingPartyApplicationDbStore();
		}

		#region IAuthorizationServer Members

		public ICryptoKeyStore CryptoKeyStore { get; private set; }

		/// <summary>
		/// Gets the authorization code nonce store to use to ensure that authorization codes can only be used once.
		/// </summary>
		/// <value>The authorization code nonce store.</value>
		public INonceStore VerificationCodeNonceStore {
			get { return this.nonceStore; }
		}

		public RSACryptoServiceProvider CreateAccessTokenSigningCryptoServiceProvider() {
			return CreateAsymmetricKeyServiceProvider();
		}

		/// <summary>
		/// Gets the client with a given identifier.
		/// </summary>
		/// <param name="clientIdentifier">The client identifier.</param>
		/// <returns>The client registration.  Never null.</returns>
		/// <exception cref="ArgumentException">Thrown when no client with the given identifier is registered with this authorization server.</exception>
		public IConsumerDescription GetClient(string clientIdentifier) {
			try {
				return Database.DataContext.Clients.First(c => c.ClientIdentifier == clientIdentifier);
			} catch (InvalidOperationException ex) {
				throw new ArgumentOutOfRangeException("No client by that identifier.", ex);
			}
		}

		/// <summary>
		/// Determines whether a described authorization is (still) valid.
		/// </summary>
		/// <param name="authorization">The authorization.</param>
		/// <returns>
		/// 	<c>true</c> if the original authorization is still valid; otherwise, <c>false</c>.
		/// </returns>
		/// <remarks>
		/// 	<para>When establishing that an authorization is still valid,
		/// it's very important to only match on recorded authorizations that
		/// meet these criteria:</para>
		/// 1) The client identifier matches.
		/// 2) The user account matches.
		/// 3) The scope on the recorded authorization must include all scopes in the given authorization.
		/// 4) The date the recorded authorization was issued must be <em>no later</em> that the date the given authorization was issued.
		/// <para>One possible scenario is where the user authorized a client, later revoked authorization,
		/// and even later reinstated authorization.  This subsequent recorded authorization
		/// would not satisfy requirement #4 in the above list.  This is important because the revocation
		/// the user went through should invalidate all previously issued tokens as a matter of
		/// security in the event the user was revoking access in order to sever authorization on a stolen
		/// account or piece of hardware in which the tokens were stored. </para>
		/// </remarks>
		public bool IsAuthorizationValid(IAuthorizationDescription authorization) {
			return this.IsAuthorizationValid(authorization.Scope, authorization.ClientIdentifier, authorization.UtcIssued, authorization.User);
		}

		#endregion

		public bool CanBeAutoApproved(EndUserAuthorizationRequest authorizationRequest) {
			if (authorizationRequest == null) {
				throw new ArgumentNullException("authorizationRequest");
			}

			// NEVER issue an auto-approval to a client that would end up getting an access token immediately
			// (without a client secret), as that would allow ANY client to spoof an approved client's identity
			// and obtain unauthorized access to user data.
			if (authorizationRequest.ResponseType == EndUserAuthorizationResponseType.AuthorizationCode) {
				// Never issue auto-approval if the client secret is blank, since that too makes it easy to spoof
				// a client's identity and obtain unauthorized access.
				var requestingClient = Database.DataContext.Clients.First(c => c.ClientIdentifier == authorizationRequest.ClientIdentifier);
				if (!string.IsNullOrEmpty(requestingClient.ClientSecret)) {
					return this.IsAuthorizationValid(
						authorizationRequest.Scope,
						authorizationRequest.ClientIdentifier,
						DateTime.UtcNow,
						HttpContext.Current.User.Identity.Name);
				}
			}

			// Default to not auto-approving.
			return false;
		}

		/// <summary>
		/// Creates the asymmetric crypto service provider.
		/// </summary>
		/// <returns>An RSA crypto service provider.</returns>
		/// <remarks>
		/// Since <see cref="RSACryptoServiceProvider"/> are not thread-safe, one must be created for each thread.
		/// In this sample we just create one for each incoming request.  Be sure to call Dispose on them to release native handles.
		/// </remarks>
		internal static RSACryptoServiceProvider CreateAsymmetricKeyServiceProvider() {
			var serviceProvider = new RSACryptoServiceProvider();
			serviceProvider.ImportParameters(AsymmetricKey);
			return serviceProvider;
		}

		/// <summary>
		/// Creates the RSA key used by all the crypto service provider instances we create.
		/// </summary>
		/// <returns>RSA data that includes the private key.</returns>
		private static RSAParameters CreateRSAKey() {
			// As we generate a new random key, we need to set the UseMachineKeyStore flag so that this doesn't
			// crash on IIS. For more information: 
			// http://social.msdn.microsoft.com/Forums/en-US/clr/thread/7ea48fd0-8d6b-43ed-b272-1a0249ae490f?prof=required
			var cspParameters = new CspParameters();
			cspParameters.Flags = CspProviderFlags.UseArchivableKey | CspProviderFlags.UseMachineKeyStore;
			var asymmetricKey = new RSACryptoServiceProvider(cspParameters);
			return asymmetricKey.ExportParameters(true);
		}

		private bool IsAuthorizationValid(HashSet<string> requestedScopes, string clientIdentifier, DateTime issuedUtc, string username) {
			var grantedScopeStrings = from auth in Database.DataContext.ClientAuthorizations
									  where
										auth.Client.ClientIdentifier == clientIdentifier &&
										auth.CreatedOnUtc <= issuedUtc &&
										(!auth.ExpirationDateUtc.HasValue || auth.ExpirationDateUtc.Value >= DateTime.UtcNow) &&
										auth.User.AuthenticationTokens.Any(token => token.ClaimedIdentifier == username)
										select auth.Scope;

			if (!grantedScopeStrings.Any()) {
				// No granted authorizations prior to the issuance of this token, so it must have been revoked.
				// Even if later authorizations restore this client's ability to call in, we can't allow
				// access tokens issued before the re-authorization because the revoked authorization should
				// effectively and permanently revoke all access and refresh tokens.
				return false;
			}

			var grantedScopes = new HashSet<string>(OAuthUtilities.ScopeStringComparer);
			foreach (string scope in grantedScopeStrings) {
				grantedScopes.UnionWith(OAuthUtilities.SplitScopes(scope));
			}

			return requestedScopes.IsSubsetOf(grantedScopes);
		}
	}
}
