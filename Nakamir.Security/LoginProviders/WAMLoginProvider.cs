﻿// <copyright file="WAMLoginProvider.cs" company="Nakamir, Inc.">
// Copyright (c) Nakamir, Inc. All rights reserved.
// </copyright>
namespace Nakamir.Security;

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

#if WINDOWS_UWP
using Windows.Security.Authentication.Web;
using Windows.Security.Authentication.Web.Core;
using Windows.Security.Credentials;
#endif

public class WAMLoginProvider(IAADLogger logger, IUserStore userStore, string clientId, string tenantId, string resource, bool biometricsRequired = true) : BaseLoginProvider(logger, userStore, clientId, tenantId, resource)
{
    public bool BiometricsRequired { get; set; } = biometricsRequired;

    public override string UserIdKey
    {
        get
        {
            return LoginHint is null ? "UserIdWAM" : $"UserIdWAM_{LoginHint}";
        }
    }

    public override string Description => $"Contains core methods for obtaining tokens from web account providers.";

    public override string ProviderName => $"WebAuthenticationCoreManager";

    public async override Task<IToken> LoginAsync(string[] scopes)
    {
        Logger.Log("Logging in with WebAuthenticationCoreManager...");

        string accessToken = string.Empty;
#if WINDOWS_UWP
        if (BiometricsRequired)
        {
            if (await Windows.Security.Credentials.UI.UserConsentVerifier.CheckAvailabilityAsync() == Windows.Security.Credentials.UI.UserConsentVerifierAvailability.Available)
            {

                TaskCompletionSource<Windows.Security.Credentials.UI.UserConsentVerificationResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        tcs.SetResult(await Windows.Security.Credentials.UI.UserConsentVerifier.RequestVerificationAsync("Please verify your credentials"));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }).AsTask();
                var consentResult = await tcs.Task;
                if (consentResult != Windows.Security.Credentials.UI.UserConsentVerificationResult.Verified)
                {
                    Logger.Log("Biometric verification failed.");
                    return null;
                }
            }
            else
            {
                Logger.Log("Biometric verification is not available or not configured.");
                return null;
            }
        }

        string userId = Store.GetUserId(UserIdKey);
        Logger.Log("User Id: " + userId);

        string URI = string.Format("ms-appx-web://Microsoft.AAD.BrokerPlugIn/{0}",
            WebAuthenticationBroker.GetCurrentApplicationCallbackUri().Host.ToUpper());
        Logger.Log("Redirect URI: " + URI);

        WebAccountProvider wap =
            await WebAuthenticationCoreManager.FindAccountProviderAsync("https://login.microsoft.com", Authority);

        Logger.Log($"Found Web Account Provider for organizations: {wap.DisplayName}");

        //var accts = await WebAuthenticationCoreManager.FindAllAccountsAsync(wap);

        //Logger.Log($"Find All Accounts Status = {accts.Status}");

        //if (accts.Status == FindAllWebAccountsStatus.Success)
        //{
        //    foreach (var acct in accts.Accounts)
        //    {
        //        Logger.Log($"Account: {acct.UserName} {acct.State.ToString()}");
        //    }
        //}

        //var sap = await WebAuthenticationCoreManager.FindSystemAccountProviderAsync(wap.Id);
        //if (sap != null)
        //{
        //    string displayName = "Not Found";
        //    if (sap.User != null)
        //    {
        //        displayName = (string)await sap.User.GetPropertyAsync("DisplayName");
        //        Logger.Log($"Found system account provider {sap.DisplayName} with user {displayName} {sap.User.AuthenticationStatus.ToString()}");
        //    }
        //}

        Logger.Log("Web Account Provider: " + wap.DisplayName);

        //string resource = "https://sts.mixedreality.azure.com";
        //https://sts.mixedreality.azure.com/mixedreality.signin 

        string scope = string.Join(' ', scopes.Select(Uri.EscapeDataString));

        WebTokenRequest wtr = new WebTokenRequest(wap, scope, ClientId);
        wtr.Properties.Add("resource", Resource);

        WebAccount account = null;

        if (!string.IsNullOrEmpty((string)userId))
        {
            account = await WebAuthenticationCoreManager.FindAccountAsync(wap, userId);
            if (account != null)
            {
                Logger.Log("Found account: " + account.UserName);
            }
            else
            {
                Logger.Log("Account not found");
            }
        }

        WebTokenRequestResult tokenResponse = null;
        try
        {
            if (account != null)
            {
                tokenResponse = await WebAuthenticationCoreManager.GetTokenSilentlyAsync(wtr, account);
            }
            else
            {
                tokenResponse = await WebAuthenticationCoreManager.GetTokenSilentlyAsync(wtr);
                //tokenResponse = await WebAuthenticationCoreManager.RequestTokenAsync(wtr);
            }
        }
        catch (Exception ex)
        {
            Logger.Log(ex.Message);
        }

        Logger.Log("Silent Token Response: " + tokenResponse.ResponseStatus.ToString());
        if (tokenResponse.ResponseError != null)
        {
            Logger.Log("Error Code: " + tokenResponse.ResponseError.ErrorCode.ToString());
            Logger.Log("Error Msg: " + tokenResponse.ResponseError.ErrorMessage.ToString());
            foreach (var errProp in tokenResponse.ResponseError.Properties)
            {
                Logger.Log($"Error prop: ({errProp.Key}, {errProp.Value})");
            }
        }

        if (tokenResponse.ResponseStatus == WebTokenRequestStatus.UserInteractionRequired)
        {
            WebTokenRequestResult wtrr = null;
            try
            {
                TaskCompletionSource<WebTokenRequestResult> tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
                await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                {
                    try
                    {
                        tcs.SetResult(account is not null
                            ? await WebAuthenticationCoreManager.RequestTokenAsync(wtr, account)
                            : await WebAuthenticationCoreManager.RequestTokenAsync(wtr));
                    }
                    catch (Exception ex)
                    {
                        tcs.SetException(ex);
                    }
                }).AsTask();
                wtrr = await tcs.Task;
            }
            catch (Exception ex)
            {
                Logger.Log(ex.Message);
            }

            if (wtrr is not null)
            {
                Logger.Log("Interactive Token Response: " + wtrr.ResponseStatus.ToString());
                if (wtrr.ResponseError != null)
                {
                    Logger.Log("Error Code: " + wtrr.ResponseError.ErrorCode.ToString());
                    Logger.Log("Error Msg: " + wtrr.ResponseError.ErrorMessage.ToString());
                    foreach (var errProp in wtrr.ResponseError.Properties)
                    {
                        Logger.Log($"Error prop: ({errProp.Key}, {errProp.Value})");
                    }
                }

                if (wtrr.ResponseStatus == WebTokenRequestStatus.Success)
                {
                    accessToken = wtrr.ResponseData[0].Token;
                    account = wtrr.ResponseData[0].WebAccount;
                    var properties = wtrr.ResponseData[0].Properties;
                    Username = account.UserName;
                    Logger.Log($"Username = {Username}");
                    var ras = await account.GetPictureAsync(WebAccountPictureSize.Size64x64);
                    var stream = ras.AsStreamForRead();
                    var br = new BinaryReader(stream);
                    UserPicture = br.ReadBytes((int)stream.Length);

                    Logger.Log("Access Token: " + accessToken, false);
                }
            }
        }

        if (tokenResponse.ResponseStatus == WebTokenRequestStatus.Success)
        {
            foreach (var resp in tokenResponse.ResponseData)
            {
                var name = resp.WebAccount.UserName;
                accessToken = resp.Token;
                account = resp.WebAccount;
                Username = account.UserName;
                Logger.Log($"Username = {Username}");
                try
                {
                    var ras = await account.GetPictureAsync(WebAccountPictureSize.Size64x64);
                    var stream = ras.AsStreamForRead();
                    var br = new BinaryReader(stream);
                    UserPicture = br.ReadBytes((int)stream.Length);
                }
                catch (Exception ex)
                {
                    Logger.Log($"Exception when reading image {ex.Message}");
                }
            }

            Logger.Log("Access Token: " + accessToken, true);
        }

        if (account != null && !string.IsNullOrEmpty(account.Id))
        {
            Store.SaveUser(UserIdKey, account.Id);
        }
#endif
        AADToken = accessToken;
        return new AADToken(AADToken);
    }

    public override async Task SignOutAsync()
    {
        Logger.Clear();
        string userId = Store.GetUserId(UserIdKey);
        if (!string.IsNullOrEmpty((string)userId))
        {
#if WINDOWS_UWP
            WebAccountProvider wap =
                await WebAuthenticationCoreManager.FindAccountProviderAsync("https://login.microsoft.com", Authority);
            var account = await WebAuthenticationCoreManager.FindAccountAsync(wap, userId);
            Logger.Log($"Found account: {account.UserName} State: {account.State}");
            await account.SignOutAsync();
#endif
            Store.ClearUser(UserIdKey);
            Username = string.Empty;
            AADToken = string.Empty;
            AccessToken = string.Empty;
        }
    }
}
