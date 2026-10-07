import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { EventoTrilha, PaginaComTotal } from '../modelos';

/** A trilha de auditoria (AUD-1). Só dono e gestor recebem 200 — a regra vive no servidor; aqui
 *  a tela apenas evita pedir o que sabe que vai ser recusado. */
@Injectable({ providedIn: 'root' })
export class TrilhaServico {
  private http = inject(HttpClient);

  /** Uma página do histórico, do mais recente para trás, com o total do servidor (AUD-XX). */
  doContato(id: number, pagina: number, tamanho: number): Observable<PaginaComTotal<EventoTrilha>> {
    return this.http.get<PaginaComTotal<EventoTrilha>>(`${API}/trilha/contato/${id}`,
      { params: { pagina, tamanho } });
  }
}
