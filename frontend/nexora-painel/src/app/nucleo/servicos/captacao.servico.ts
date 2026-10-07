import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { ResumoCaptacao } from '../modelos';

/** O resumo do topo da Captação (AUD-XX): uma chamada, os números prontos. */
@Injectable({ providedIn: 'root' })
export class CaptacaoServico {
  private http = inject(HttpClient);

  resumo(): Observable<ResumoCaptacao> {
    return this.http.get<ResumoCaptacao>(`${API}/captacao/resumo`);
  }
}
