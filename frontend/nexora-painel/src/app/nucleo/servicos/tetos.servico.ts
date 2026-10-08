import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { TetosDaEmpresa } from '../modelos';

/** Os tetos da empresa e quanto está em uso, do servidor (AUD-XX). Com `pipelineId`, vem também o
 *  das etapas daquele funil. */
@Injectable({ providedIn: 'root' })
export class TetosServico {
  private http = inject(HttpClient);

  obter(pipelineId: number | null = null): Observable<TetosDaEmpresa> {
    const opcoes = pipelineId === null ? {} : { params: { pipeline: pipelineId } };
    return this.http.get<TetosDaEmpresa>(`${API}/limites`, opcoes);
  }
}
