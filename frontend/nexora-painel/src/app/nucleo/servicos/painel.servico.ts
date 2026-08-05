import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { StatusPainel } from '../modelos';

/** O payload BARATO do shell (badge + banner + limites do semáforo).
 *
 *  Separado do dashboard rico de propósito: este aqui roda em polling e não pode carregar
 *  funil, séries nem agregação. */
@Injectable({ providedIn: 'root' })
export class PainelServico {
  private http = inject(HttpClient);

  status(): Observable<StatusPainel> {
    return this.http.get<StatusPainel>(`${API}/painel/status`);
  }
}
