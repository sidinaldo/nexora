import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { API } from '../api-base';
import { StatusPainel } from '../modelos';

/** O payload BARATO do shell (badge + banner + limites do semáforo).
 *
 *  Separado do dashboard rico de propósito: este aqui roda em polling e não pode carregar
 *  funil, séries nem agregação. */
@Injectable({ providedIn: 'root' })
export class PainelServico {
  private http = inject(HttpClient);

  /** A última resposta, para quem precisa do estado sem disparar outra requisição.
   *
   *  O shell já busca isto no boot e em polling. O dashboard precisa do mesmo fato para não
   *  mandar conectar um WhatsApp que já está conectado — e uma segunda chamada ao mesmo
   *  endpoint, no mesmo instante, seria trabalho repetido para responder o que já está aqui.
   *
   *  `null` significa AINDA NÃO SEI, e é diferente de `false`. Quem lê tem que tratar os três
   *  casos: afirmar "desconectado" antes da primeira resposta é a origem do bug que isto
   *  conserta. */
  ultimo = signal<StatusPainel | null>(null);

  status(): Observable<StatusPainel> {
    return this.http.get<StatusPainel>(`${API}/painel/status`)
      .pipe(tap(s => this.ultimo.set(s)));
  }
}
