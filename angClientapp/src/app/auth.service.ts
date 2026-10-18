import { Injectable, inject } from '@angular/core';
import { MsalService } from '@azure/msal-angular';

@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly msal = inject(MsalService);

  get account() {
    return this.msal.instance.getActiveAccount() ?? this.msal.instance.getAllAccounts()[0] ?? null;
  }

  get isSignedIn(): boolean {
    return this.account !== null;
  }

  get userName(): string {
    return this.account?.name ?? this.account?.username ?? '';
  }

  get roles(): string[] {
    const roles = this.account?.idTokenClaims?.['roles'];
    return Array.isArray(roles) ? (roles as string[]) : [];
  }

  get canWrite(): boolean {
    return this.hasAny('Contributor', 'Reviewer', 'Admin');
  }

  get canReview(): boolean {
    return this.hasAny('Reviewer', 'Admin');
  }

  login(): void {
    void this.msal.loginRedirect();
  }

  logout(): void {
    void this.msal.logoutRedirect();
  }

  private hasAny(...allowed: string[]): boolean {
    return this.roles.some(r => allowed.includes(r));
  }
}
