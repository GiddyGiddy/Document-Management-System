import { Routes } from '@angular/router';

import { MsalGuard } from '@azure/msal-angular';
import { DocumentRevisions } from './document-revisions/document-revisions';

export const routes: Routes = [
  { path: 'documents/:id/revisions', component: DocumentRevisions, canActivate: [MsalGuard] }
];
