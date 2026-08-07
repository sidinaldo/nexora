import { HttpClient, HttpParams, HttpEvent } from '@angular/common/http';
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

  /** UMA conversa, pelo id.
   *
   *  A lista é por CURSOR e o cliente carrega só a primeira página. Quem chega de fora — Meu Dia
   *  ou detalhe do contato, com `?conversa=N` — precisa abrir a conversa esteja ela onde estiver.
   *  Procurar rolando não serve: a lista se reordena em tempo real e o alvo pode nunca aparecer.
   *
   *  404 tanto para inexistente quanto para conversa de outra empresa. */
  conversa(conversaId: number): Observable<ConversaResumo> {
    return this.http.get<ConversaResumo>(`${this.base}/${conversaId}`);
  }

  /** A thread, por cursor: as `tamanho` mensagens mais novas antes de `antes`
   *  (undefined = as últimas). */
  mensagens(conversaId: number, antes?: number, tamanho = 30): Observable<PaginaCursor<MensagemDto>> {
    let p = new HttpParams().set('tamanho', tamanho);
    if (antes != null) p = p.set('antes', antes);
    return this.http.get<PaginaCursor<MensagemDto>>(`${this.base}/${conversaId}/mensagens`, { params: p });
  }

  /** Envia imagem ou PDF (MID-1).
   *
   *  `reportProgress` + `observe: 'events'` porque um PDF de 8 MB numa conexão de operação leva
   *  segundos, e barra parada faz o vendedor clicar de novo — que é como se manda duas vezes.
   *
   *  NADA de `Content-Type` manual: o navegador precisa gerar o `boundary` do multipart. */
  enviarMidia(conversaId: number, arquivo: File, legenda: string): Observable<HttpEvent<RespostaEnviada>> {
    const corpo = new FormData();
    corpo.append('arquivo', arquivo, arquivo.name);
    if (legenda.trim()) corpo.append('legenda', legenda.trim());

    return this.http.post<RespostaEnviada>(`${this.base}/${conversaId}/midia`, corpo, {
      reportProgress: true,
      observe: 'events'
    });
  }

  /** Nota de voz (bloco 13). O servidor decide o formato final — ver `AudioOpus`. */
  enviarAudio(conversaId: number, audio: Blob, nome: string): Observable<RespostaEnviada> {
    const corpo = new FormData();
    corpo.append('arquivo', audio, nome);
    return this.http.post<RespostaEnviada>(`${this.base}/${conversaId}/audio`, corpo);
  }

  /** Tentar de novo. REAPROVEITA a linha que falhou — o servidor recusa se já foi enviada. */
  reenviar(mensagemId: number): Observable<RespostaEnviada> {
    return this.http.post<RespostaEnviada>(`${this.base}/mensagens/${mensagemId}/reenviar`, {});
  }

  /** O binário da mídia, como BLOB.
   *
   *  ⚠️ NÃO dá para usar `<img src="/api/midia/1">`: a rota é autenticada por Bearer, e `<img>`
   *  não manda cabeçalho. Buscar como blob passa pelo interceptor de auth e vira `blob:` local —
   *  e é isso que permite manter a rota fechada em vez de abrir uma pública para servir arquivo.
   */
  midia(mensagemId: number): Observable<Blob> {
    return this.http.get(`${API}/midia/${mensagemId}`, { responseType: 'blob' });
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
