import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { ConversaResumo, FiltroConversa, MensagemDto, PaginaCursor, RespostaEnviada } from '../modelos';

/** A caixa de entrada. Um contato = uma conversa (1:1 na fase 1). */
@Injectable({ providedIn: 'root' })
export class CaixaServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/conversas`;

  /** A lista, paginada por CURSOR (cursorEm = ultimaMensagemEm + cursorId = id do último
   *  item; ambos ausentes = primeira página). */
  conversas(filtro: FiltroConversa, busca?: string, cursorEm?: string | null,
            cursorId?: number | null, tamanho = 30): Observable<PaginaCursor<ConversaResumo>> {
    let p = new HttpParams().set('filtro', filtro).set('tamanho', tamanho);
    if (busca) p = p.set('busca', busca);
    if (cursorEm) p = p.set('cursorEm', cursorEm);
    if (cursorId != null) p = p.set('cursorId', cursorId);
    return this.http.get<PaginaCursor<ConversaResumo>>(this.base, { params: p });
  }

  /** A thread, por cursor: as `tamanho` mensagens mais novas antes de `antes`
   *  (undefined = as últimas). */
  mensagens(conversaId: number, antes?: number, tamanho = 30): Observable<PaginaCursor<MensagemDto>> {
    let p = new HttpParams().set('tamanho', tamanho);
    if (antes != null) p = p.set('antes', antes);
    return this.http.get<PaginaCursor<MensagemDto>>(`${this.base}/${conversaId}/mensagens`, { params: p });
  }

  /** Responder. Se a conversa não tinha dono, responder ATRIBUI ao vendedor. */
  responder(conversaId: number, texto: string): Observable<RespostaEnviada> {
    return this.http.post<RespostaEnviada>(`${this.base}/${conversaId}/responder`, { texto });
  }

  marcarLida(conversaId: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${conversaId}/lida`, {});
  }

  /** Assumir conversa de outro devolve 409. */
  assumir(conversaId: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${conversaId}/assumir`, {});
  }

  liberar(conversaId: number): Observable<void> {
    return this.http.post<void>(`${this.base}/${conversaId}/liberar`, {});
  }
}
