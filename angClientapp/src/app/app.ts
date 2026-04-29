import { Component, signal,inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { FileUpload } from './file-upload/file-upload';
@Component({
  selector: 'app-root',
  imports: [FileUpload],
  templateUrl: './app.html',
  styleUrl: './app.css'
})
export class App {

  protected readonly title = signal('Document Management System');


  constructor(){
    console.log("App constructor");
   
   
  }
  
}
