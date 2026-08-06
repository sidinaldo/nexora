import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { Canais, OrigemLead } from '../modelos';

/** Os canais de captação por QR Code e link rastreável.
 *
 *  O QR vem DESENHADO do servidor (SVG e PNG), não como URL para uma API de terceiro montar a
 *  imagem: material impresso não pode depender da disponibilidade de outra empresa, e o número
 *  de WhatsApp do cliente não vai para servidor de ninguém.
 *
 *  O download é por BLOB porque as rotas do painel exigem `Authorization: Bearer` — um
 *  `<a href="/api/...">` navegaria sem cabeçalho e abriria um 401. */
@Injectable({ providedIn: 'root' })
export class CanaisServico {
  private http = inject(HttpClient);
  private readonly base = `${API}/canais`;

  listar(): Observable<Canais> {
    return this.http.get<Canais>(this.base);
  }

  criar(nome: string, conexaoId: number, origem: OrigemLead): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(this.base, { nome, conexaoId, origem });
  }

  /** O CÓDIGO não entra aqui, em nenhuma circunstância: ele já está impresso em papel que não
   *  volta. Trocá-lo transformaria todo material distribuído em link sem atribuição. */
  atualizar(id: number, nome: string, conexaoId: number, origem: OrigemLead): Observable<void> {
    return this.http.put<void>(`${this.base}/${id}`, { nome, conexaoId, origem });
  }

  alternarAtivo(id: number, ativo: boolean): Observable<void> {
    return this.http.post<void>(`${this.base}/${id}/ativo`, { ativo });
  }

  remover(id: number): Observable<void> {
    return this.http.delete<void>(`${this.base}/${id}`);
  }

  svg(id: number): Observable<Blob> {
    return this.http.get(`${this.base}/${id}/qr.svg`, { responseType: 'blob' });
  }

  png(id: number): Observable<Blob> {
    return this.http.get(`${this.base}/${id}/qr.png`, { responseType: 'blob' });
  }
}
