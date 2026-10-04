import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { EventoTrilha } from '../modelos';

/** A trilha de auditoria (AUD-1). Só dono e gestor recebem 200 — a regra vive no servidor; aqui
 *  a tela apenas evita pedir o que sabe que vai ser recusado. */
@Injectable({ providedIn: 'root' })
export class TrilhaServico {
  private http = inject(HttpClient);

  /** @param tamanho quantos eventos trazer, do mais recente para trás. O servidor tem padrão 50 e
   *  teto de 200 (`ServicoTrilha`) — pedir mais que isso devolve 200, não erro. */
  doContato(id: number, tamanho?: number): Observable<EventoTrilha[]> {
    return this.http.get<EventoTrilha[]>(`${API}/trilha/contato/${id}`,
      tamanho ? { params: { tamanho } } : {});
  }
}
