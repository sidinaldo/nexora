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

  doContato(id: number): Observable<EventoTrilha[]> {
    return this.http.get<EventoTrilha[]>(`${API}/trilha/contato/${id}`);
  }
}
