import { ApplicationConfig, inject, provideAppInitializer, provideBrowserGlobalErrorListeners } from '@angular/core';
import { provideRouter } from '@angular/router';
import { HTTP_INTERCEPTORS, provideHttpClient, withFetch, withInterceptorsFromDi } from '@angular/common/http';
import { BrowserCacheLocation, InteractionType, IPublicClientApplication, PublicClientApplication } from '@azure/msal-browser';
import {
  MSAL_GUARD_CONFIG,
  MSAL_INSTANCE,
  MSAL_INTERCEPTOR_CONFIG,
  MsalBroadcastService,
  MsalGuard,
  MsalGuardConfiguration,
  MsalInterceptor,
  MsalInterceptorConfiguration,
  MsalService,
} from '@azure/msal-angular';

import { routes } from './app.routes';
import { environment } from '../environments/environment.development';

export function msalInstanceFactory(): IPublicClientApplication {
  return new PublicClientApplication({
    auth: {
      clientId: environment.entra.spaClientId,
      authority: `https://login.microsoftonline.com/${environment.entra.tenantId}`,
      redirectUri: environment.entra.redirectUri,
      postLogoutRedirectUri: environment.entra.redirectUri,
    },
    cache: { cacheLocation: BrowserCacheLocation.SessionStorage },
  });
}

export function msalGuardConfigFactory(): MsalGuardConfiguration {
  return {
    interactionType: InteractionType.Redirect,
    authRequest: { scopes: [environment.entra.apiScope] },
  };
}

export function msalInterceptorConfigFactory(): MsalInterceptorConfiguration {
  return {
    interactionType: InteractionType.Redirect,
    protectedResourceMap: new Map([[environment.apiURL, [environment.entra.apiScope]]]),
  };
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideRouter(routes),
    provideHttpClient(withFetch(), withInterceptorsFromDi()),
    { provide: HTTP_INTERCEPTORS, useClass: MsalInterceptor, multi: true },
    { provide: MSAL_INSTANCE, useFactory: msalInstanceFactory },
    { provide: MSAL_GUARD_CONFIG, useFactory: msalGuardConfigFactory },
    { provide: MSAL_INTERCEPTOR_CONFIG, useFactory: msalInterceptorConfigFactory },
    MsalService,
    MsalGuard,
    MsalBroadcastService,
    provideAppInitializer(async () => {
      const instance = inject(MSAL_INSTANCE);
      await instance.initialize();
      const result = await instance.handleRedirectPromise();
      const account = result?.account ?? instance.getAllAccounts()[0];
      if (account) {
        instance.setActiveAccount(account);
      }
    }),
  ]
};
