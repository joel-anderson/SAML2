﻿using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.IdentityModel.Extensions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.Owin.Logging;
using Microsoft.Owin.Security.Infrastructure;
using Microsoft.Owin.Security.Notifications;
using Microsoft.Owin.Security;
using SAML2.Config;

namespace Owin.Security.Saml
{
    /// <summary>
    /// A per-request authentication handler for the SamlAuthenticationMiddleware.
    /// </summary>
    public class SamlAuthenticationHandler : AuthenticationHandler<SamlAuthenticationOptions>
    {
        private const string HandledResponse = "HandledResponse";

        private readonly ILogger _logger;

        /// <summary>
        /// Creates a new SamlAuthenticationHandler
        /// </summary>
        /// <param name="logger"></param>
        public SamlAuthenticationHandler(ILogger logger)
        {
            _logger = logger;
        }

        /// <summary>
        /// Handles Signout
        /// </summary>
        /// <returns></returns>
        protected override async Task ApplyResponseGrantAsync()
        {
            AuthenticationResponseRevoke signout = Helper.LookupSignOut(Options.AuthenticationType, Options.AuthenticationMode);
            if (signout == null)
            {
                return;
            }

            SamlMessage SamlMessage = await SamlMessageFromRequest();

            // WS Fed was "TokenAddress". Not sure this is the right endpoint
            SamlMessage.IssuerAddress = Options.Configuration.ServiceProvider.Endpoints.DefaultLogoutEndpoint.RedirectUrl ?? string.Empty;
            SamlMessage.Reply = string.Empty;

            // Set Wreply in order:
            // 1. properties.Redirect
            // 2. Options.SignOutWreply
            // 3. Options.Wreply
            AuthenticationProperties properties = signout.Properties;
            if (properties != null && !string.IsNullOrEmpty(properties.RedirectUri))
            {
                SamlMessage.Reply = properties.RedirectUri;
            }
            else if (!string.IsNullOrWhiteSpace(Options.Configuration.ServiceProvider.Endpoints.DefaultLogoutEndpoint.RedirectUrl))
            {
                SamlMessage.Reply = Options.Configuration.ServiceProvider.Endpoints.DefaultLogoutEndpoint.RedirectUrl;
            }

            var notification = new RedirectToIdentityProviderNotification<SamlMessage, SamlAuthenticationOptions>(Context, Options)
            {
                ProtocolMessage = SamlMessage
            };
            await Options.Notifications.RedirectToIdentityProvider(notification);

            if (!notification.HandledResponse)
            {
                string redirectUri = notification.ProtocolMessage.BuildRedirectUrl();
                if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
                {
                    _logger.WriteWarning("The sign-out redirect URI is malformed: " + redirectUri);
                }
                Response.Redirect(redirectUri);
            }
        }

        /// <summary>
        /// Handles Challenge
        /// </summary>
        /// <returns></returns>
        protected override async Task ApplyResponseChallengeAsync()
        {
            if (Response.StatusCode != 401)
            {
                return;
            }

            AuthenticationResponseChallenge challenge = Helper.LookupChallenge(Options.AuthenticationType, Options.AuthenticationMode);
            if (challenge == null)
            {
                return;
            }

            string baseUri =
                    Request.Scheme +
                    Uri.SchemeDelimiter +
                    Request.Host +
                    Request.PathBase;

            string currentUri =
                baseUri +
                Request.Path +
                Request.QueryString;

            // Save the original challenge URI so we can redirect back to it when we're done.
            AuthenticationProperties properties = challenge.Properties;
            if (string.IsNullOrEmpty(properties.RedirectUri))
            {
                properties.RedirectUri = currentUri;
            }

            SamlMessage SamlMessage = await SamlMessageFromRequest();
            {
                //IssuerAddress = _configuration.TokenEndpoint ?? string.Empty,
                //Wtrealm = Options.Wtrealm,
                //Wctx = SamlAuthenticationDefaults.WctxKey + "=" + Uri.EscapeDataString(Options.StateDataFormat.Protect(properties)),
                //Wa = SamlActions.SignIn,
            };

            //if (!string.IsNullOrWhiteSpace(Options.Wreply))
            //{
            //    SamlMessage.Wreply = Options.Wreply;
            //}

            var notification = new RedirectToIdentityProviderNotification<SamlMessage, SamlAuthenticationOptions>(Context, Options)
            {
                ProtocolMessage = SamlMessage
            };
            await Options.Notifications.RedirectToIdentityProvider(notification);

            if (!notification.HandledResponse)
            {
                string redirectUri = notification.ProtocolMessage.BuildRedirectUrl();
                if (!Uri.IsWellFormedUriString(redirectUri, UriKind.Absolute))
                {
                    _logger.WriteWarning("The sign-in redirect URI is malformed: " + redirectUri);
                }
                Response.Redirect(redirectUri);
            }
        }

        /// <summary>
        /// Invoked to detect and process incoming authentication requests.
        /// </summary>
        /// <returns></returns>
        public override Task<bool> InvokeAsync()
        {
            return InvokeReplyPathAsync();
        }

        // Returns true if the request was handled, false if the next middleware should be invoked.
        private async Task<bool> InvokeReplyPathAsync()
        {
            AuthenticationTicket ticket = await AuthenticateAsync();
            if (ticket == null)
            {
                return false;
            }

            string value;
            if (ticket.Properties.Dictionary.TryGetValue(HandledResponse, out value) && value == "true")
            {
                return true;
            }
            if (ticket.Identity != null)
            {
                Request.Context.Authentication.SignIn(ticket.Properties, ticket.Identity);
            }
            // Redirect back to the original secured resource, if any.
            if (!string.IsNullOrWhiteSpace(ticket.Properties.RedirectUri))
            {
                Response.Redirect(ticket.Properties.RedirectUri);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Invoked to process incoming authentication messages.
        /// </summary>
        /// <returns></returns>
        protected override async Task<AuthenticationTicket> AuthenticateCoreAsync()
        {
            // Allow login to be constrained to a specific path.
            //if (Options.CallbackPath.HasValue && Options.CallbackPath != (Request.PathBase + Request.Path))
            //{
            //    return null;
            //}

            var SamlMessage = await SamlMessageFromRequest();

            if (SamlMessage == null || !SamlMessage.IsSignInMessage()) {
                return null;
            }

            ExceptionDispatchInfo authFailedEx = null;
            try {
                var messageReceivedNotification = new MessageReceivedNotification<SamlMessage, SamlAuthenticationOptions>(Context, Options)
                {
                    ProtocolMessage = SamlMessage
                };
                await Options.Notifications.MessageReceived(messageReceivedNotification);
                if (messageReceivedNotification.HandledResponse) {
                    return GetHandledResponseTicket();
                }
                if (messageReceivedNotification.Skipped) {
                    return null;
                }

                //if (SamlMessage.Wresult == null) {
                //    _logger.WriteWarning("Received a sign-in message without a WResult.");
                //    return null;
                //}

                string token = SamlMessage.GetToken();
                if (string.IsNullOrWhiteSpace(token)) {
                    _logger.WriteWarning("Received a sign-in message without a token.");
                    return null;
                }

                var securityTokenReceivedNotification = new SecurityTokenReceivedNotification<SamlMessage, SamlAuthenticationOptions>(Context, Options)
                {
                    ProtocolMessage = SamlMessage
                };
                await Options.Notifications.SecurityTokenReceived(securityTokenReceivedNotification);
                if (securityTokenReceivedNotification.HandledResponse) {
                    return GetHandledResponseTicket();
                }
                if (securityTokenReceivedNotification.Skipped) {
                    return null;
                }


                // Copy and augment to avoid cross request race conditions for updated configurations.

                ClaimsPrincipal principal = null;
                ClaimsIdentity claimsIdentity = principal.Identity as ClaimsIdentity;

                // Retrieve our cached redirect uri
                // string state = SamlMessage.Wctx;
                // WsFed allows for uninitiated logins, state may be missing.
                AuthenticationProperties properties = null;// GetPropertiesFromWctx(state);
                AuthenticationTicket ticket = new AuthenticationTicket(claimsIdentity, properties);

                //if (Options.UseTokenLifetime) {
                //    // Override any session persistence to match the token lifetime.
                //    DateTime issued = parsedToken.ValidFrom;
                //    if (issued != DateTime.MinValue) {
                //        ticket.Properties.IssuedUtc = issued.ToUniversalTime();
                //    }
                //    DateTime expires = parsedToken.ValidTo;
                //    if (expires != DateTime.MinValue) {
                //        ticket.Properties.ExpiresUtc = expires.ToUniversalTime();
                //    }
                //    ticket.Properties.AllowRefresh = false;
                //}

                var securityTokenValidatedNotification = new SecurityTokenValidatedNotification<SamlMessage, SamlAuthenticationOptions>(Context, Options)
                {
                    AuthenticationTicket = ticket,
                    ProtocolMessage = SamlMessage,
                };

                await Options.Notifications.SecurityTokenValidated(securityTokenValidatedNotification);
                if (securityTokenValidatedNotification.HandledResponse) {
                    return GetHandledResponseTicket();
                }
                if (securityTokenValidatedNotification.Skipped) {
                    return null;
                }
                // Flow possible changes
                ticket = securityTokenValidatedNotification.AuthenticationTicket;

                return ticket;
            }
            catch (Exception exception) {
                // We can't await inside a catch block, capture and handle outside.
                authFailedEx = ExceptionDispatchInfo.Capture(exception);
            }

            if (authFailedEx != null) {
                _logger.WriteError("Exception occurred while processing message: ", authFailedEx.SourceException);

                // Refresh the configuration for exceptions that may be caused by key rollovers. The user can also request a refresh in the notification.
                //if (Options.RefreshOnIssuerKeyNotFound && authFailedEx.SourceException.GetType().Equals(typeof(SecurityTokenSignatureKeyNotFoundException))) {
                //    Options.ConfigurationManager.RequestRefresh();
                //}

                var authenticationFailedNotification = new AuthenticationFailedNotification<SamlMessage, SamlAuthenticationOptions>(Context, Options)
                {
                    ProtocolMessage = SamlMessage,
                    Exception = authFailedEx.SourceException
                };
                await Options.Notifications.AuthenticationFailed(authenticationFailedNotification);
                if (authenticationFailedNotification.HandledResponse) {
                    return GetHandledResponseTicket();
                }
                if (authenticationFailedNotification.Skipped) {
                    return null;
                }

                authFailedEx.Throw();
            }

            return null;
        }

        private async Task<SamlMessage> SamlMessageFromRequest()
        {
            var samlMessage = new SamlMessage(null, Context, Options.Configuration);
            // assumption: if the ContentType is "application/x-www-form-urlencoded" it should be safe to read as it is small.
            if (string.Equals(Request.Method, "POST", StringComparison.OrdinalIgnoreCase)
              && !string.IsNullOrWhiteSpace(Request.ContentType)
              // May have media/type; charset=utf-8, allow partial match.
              && Request.ContentType.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase)
              && Request.Body.CanRead) {
                if (!Request.Body.CanSeek) {
                    _logger.WriteVerbose("Buffering request body");
                    // Buffer in case this body was not meant for us.
                    MemoryStream memoryStream = new MemoryStream();
                    await Request.Body.CopyToAsync(memoryStream);
                    memoryStream.Seek(0, SeekOrigin.Begin);
                    Request.Body = memoryStream;
                }
                var form = await Request.ReadFormAsync();
                Request.Body.Seek(0, SeekOrigin.Begin);

                // TODO: a delegate on SamlAuthenticationOptions would allow for users to hook their own custom message.
                samlMessage = new SamlMessage(form, Context, Options.Configuration);
            }

            return samlMessage;
        }

        private static AuthenticationTicket GetHandledResponseTicket()
        {
            return new AuthenticationTicket(null, new AuthenticationProperties(new Dictionary<string, string>() { { HandledResponse, "true" } }));
        }

    }
}