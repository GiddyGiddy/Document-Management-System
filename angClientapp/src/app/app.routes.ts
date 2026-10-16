import { Routes } from '@angular/router';

import { DocumentRevisions } from './document-revisions/document-revisions';

export const routes: Routes = [
  { path: 'documents/:id/revisions', component: DocumentRevisions }
];
