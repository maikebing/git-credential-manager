using System;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using GitCredentialManager;
using GitCredentialManager.Authentication;
using GitCredentialManager.Authentication.OAuth;

namespace Gitee
{
    public interface IGiteeAuthentication : IDisposable
    {
        Task<AuthenticationPromptResult> GetAuthenticationAsync(Uri targetUri, string userName, AuthenticationModes modes);

        Task<OAuth2TokenResult> GetOAuthTokenViaBrowserAsync(Uri targetUri, IEnumerable<string> scopes);

        Task<OAuth2TokenResult> GetOAuthTokenViaRefresh(Uri targetUri, string refreshToken);
    }

    public class AuthenticationPromptResult
    {
        public AuthenticationPromptResult(AuthenticationModes mode)
        {
            AuthenticationMode = mode;
        }

        public AuthenticationPromptResult(AuthenticationModes mode, ICredential credential)
            : this(mode)
        {
            Credential = credential;
        }

        public AuthenticationModes AuthenticationMode { get; }

        public ICredential Credential { get; set; }
    }

    [Flags]
    public enum AuthenticationModes
    {
        None = 0,
        Basic = 1,
        Browser = 1 << 1,
        Pat = 1 << 2,

        All = Basic | Browser | Pat
    }

    public class GiteeAuthentication : AuthenticationBase, IGiteeAuthentication
    {
        public GiteeAuthentication(ICommandContext context)
            : base(context) { }

        public async Task<AuthenticationPromptResult> GetAuthenticationAsync(Uri targetUri, string userName, AuthenticationModes modes)
        {
            // If we don't have a desktop session/GUI then we cannot offer browser
            if (!Context.SessionManager.IsDesktopSession)
            {
                modes = modes & ~AuthenticationModes.Browser;
            }

            // We need at least one mode!
            if (modes == AuthenticationModes.None)
            {
                throw new ArgumentException(@$"Must specify at least one {nameof(AuthenticationModes)}", nameof(modes));
            }

            if (Context.Settings.IsGuiPromptsEnabled && Context.SessionManager.IsDesktopSession &&
                TryFindHelperCommand(out string helperCommand, out string args))
            {
                var promptArgs = new StringBuilder(args);
                promptArgs.Append("prompt");
                if (!string.IsNullOrWhiteSpace(userName))
                {
                    promptArgs.AppendFormat(" --username {0}", QuoteCmdArg(userName));
                }

                promptArgs.AppendFormat(" --url {0}", QuoteCmdArg(targetUri.ToString()));

                if ((modes & AuthenticationModes.Basic) != 0)   promptArgs.Append(" --basic");
                if ((modes & AuthenticationModes.Browser) != 0) promptArgs.Append(" --browser");
                if ((modes & AuthenticationModes.Pat) != 0)     promptArgs.Append(" --pat");

                IDictionary<string, string> resultDict = await InvokeHelperAsync(helperCommand, promptArgs.ToString());

                if (!resultDict.TryGetValue("mode", out string responseMode))
                {
                    throw new Exception("Missing 'mode' in response");
                }

                switch (responseMode.ToLowerInvariant())
                {
                    case "pat":
                        if (!resultDict.TryGetValue("pat", out string pat))
                        {
                            throw new Exception("Missing 'pat' in response");
                        }

                        if (!resultDict.TryGetValue("username", out string patUserName))
                        {
                            // Username is optional for PATs
                        }

                        return new AuthenticationPromptResult(
                            AuthenticationModes.Pat, new GitCredential(patUserName, pat));

                    case "browser":
                        return new AuthenticationPromptResult(AuthenticationModes.Browser);

                    case "basic":
                        if (!resultDict.TryGetValue("username", out userName))
                        {
                            throw new Exception("Missing 'username' in response");
                        }

                        if (!resultDict.TryGetValue("password", out string password))
                        {
                            throw new Exception("Missing 'password' in response");
                        }

                        return new AuthenticationPromptResult(
                            AuthenticationModes.Basic, new GitCredential(userName, password));

                    default:
                        throw new Exception($"Unknown mode value in response '{responseMode}'");
                }
            }
            else
            {
                switch (modes)
                {
                    case AuthenticationModes.Basic:
                        ThrowIfUserInteractionDisabled();
                        ThrowIfTerminalPromptsDisabled();
                        Context.Terminal.WriteLine("Enter Gitee credentials for '{0}'...", targetUri);

                        if (string.IsNullOrWhiteSpace(userName))
                        {
                            userName = Context.Terminal.Prompt("Username");
                        }
                        else
                        {
                            Context.Terminal.WriteLine("Username: {0}", userName);
                        }

                        string password = Context.Terminal.PromptSecret("Password");
                        return new AuthenticationPromptResult(AuthenticationModes.Basic, new GitCredential(userName, password));

                    case AuthenticationModes.Pat:
                        ThrowIfUserInteractionDisabled();
                        ThrowIfTerminalPromptsDisabled();
                        Context.Terminal.WriteLine("Enter Gitee credentials for '{0}'...", targetUri);

                        if (string.IsNullOrWhiteSpace(userName))
                        {
                            userName = Context.Terminal.Prompt("Username");
                        }
                        else
                        {
                            Context.Terminal.WriteLine("Username: {0}", userName);
                        }

                        string token = Context.Terminal.PromptSecret("Personal access token");
                        return new AuthenticationPromptResult(AuthenticationModes.Pat, new GitCredential(userName, token));

                    case AuthenticationModes.Browser:
                        return new AuthenticationPromptResult(AuthenticationModes.Browser);

                    case AuthenticationModes.None:
                        throw new ArgumentOutOfRangeException(nameof(modes), @$"At least one {nameof(AuthenticationModes)} must be supplied");

                    default:
                        ThrowIfUserInteractionDisabled();
                        ThrowIfTerminalPromptsDisabled();
                        var menuTitle = $"Select an authentication method for '{targetUri}'";
                        var menu = new TerminalMenu(Context.Terminal, menuTitle);

                        TerminalMenuItem browserItem = null;
                        TerminalMenuItem basicItem = null;
                        TerminalMenuItem patItem = null;

                        if ((modes & AuthenticationModes.Browser) != 0) browserItem = menu.Add("Web browser");
                        if ((modes & AuthenticationModes.Pat) != 0) patItem = menu.Add("Personal access token");
                        if ((modes & AuthenticationModes.Basic) != 0) basicItem = menu.Add("Username/password");

                        // Default to the 'first' choice in the menu
                        TerminalMenuItem choice = menu.Show(0);

                        if (choice == browserItem) goto case AuthenticationModes.Browser;
                        if (choice == basicItem) goto case AuthenticationModes.Basic;
                        if (choice == patItem) goto case AuthenticationModes.Pat;

                        throw new Exception();
                }
            }
        }

        public async Task<OAuth2TokenResult> GetOAuthTokenViaBrowserAsync(Uri targetUri, IEnumerable<string> scopes)
        {
            ThrowIfUserInteractionDisabled();

            var oauthClient = new GiteeOAuth2Client(HttpClient, Context.Settings, targetUri);

            // We require a desktop session to launch the user's default web browser
            if (!Context.SessionManager.IsDesktopSession)
            {
                throw new InvalidOperationException("Browser authentication requires a desktop session");
            }

            var browserOptions = new OAuth2WebBrowserOptions { };
            var browser = new OAuth2SystemWebBrowser(Context.Environment, browserOptions);

            // Write message to the terminal (if any is attached) for some feedback that we're waiting for a web response
            Context.Terminal.WriteLine("info: please complete authentication in your browser...");

            OAuth2AuthorizationCodeResult authCodeResult =
                await oauthClient.GetAuthorizationCodeAsync(scopes, browser, CancellationToken.None);

            return await oauthClient.GetTokenByAuthorizationCodeAsync(authCodeResult, CancellationToken.None);
        }

        public async Task<OAuth2TokenResult> GetOAuthTokenViaRefresh(Uri targetUri, string refreshToken)
        {
            var oauthClient = new GiteeOAuth2Client(HttpClient, Context.Settings, targetUri);
            return await oauthClient.GetTokenByRefreshTokenAsync(refreshToken, CancellationToken.None);
        }

        private bool TryFindHelperCommand(out string command, out string args)
        {
            return TryFindHelperCommand(
                GiteeConstants.EnvironmentVariables.AuthenticationHelper,
                GiteeConstants.GitConfiguration.Credential.AuthenticationHelper,
                GiteeConstants.DefaultAuthenticationHelper,
                out command,
                out args);
        }

        private HttpClient _httpClient;
        private HttpClient HttpClient => _httpClient ?? (_httpClient = Context.HttpClientFactory.CreateClient());

        public void Dispose()
        {
            _httpClient?.Dispose();
        }
    }
}
