import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';
import { EtapaConfigDto } from '../modelos';

/** Configuração do funil. Só o DONO — a API devolve 403 para os outros papéis.
 *
 *  A LEITURA do quadro continua no `FunilServico`: lá é operação diária, aqui é configuração. */
@Injectable({ providedIn: 'root' })
export class EtapasServico {
  private http = inject(HttpClient);

  listar(): Observable<EtapaConfigDto[]> {
    return this.http.get<EtapaConfigDto[]>(`${API}/etapas`);
  }

  criar(nome: string, cor: string | null): Observable<{ id: number }> {
    return this.http.post<{ id: number }>(`${API}/etapas`, { nome, cor });
  }

  atualizar(id: number, nome: string, cor: string | null): Observable<void> {
    return this.http.put<void>(`${API}/etapas/${id}`, { nome, cor });
  }

  /** Manda a ordem INTEIRA, não um "sobe uma posição".
   *
   *  É o que torna a operação idempotente: repetir a requisição por duplo clique ou retry de
   *  rede dá o mesmo resultado. "Sobe uma" aplicado duas vezes moveria a coluna duas casas. */
  reordenar(ids: number[]): Observable<void> {
    return this.http.put<void>(`${API}/etapas/ordem`, { ids });
  }

  definirGanho(id: number): Observable<void> {
    return this.http.post<void>(`${API}/etapas/${id}/ganho`, {});
  }

  /** `destino` é obrigatório quando a etapa tem contatos — a API recusa sem ele, e a tela
   *  pergunta antes justamente para o dono não levar o erro depois do clique. */
  remover(id: number, destino: number | null): Observable<void> {
    const query = destino === null ? '' : `?destino=${destino}`;
    return this.http.delete<void>(`${API}/etapas/${id}${query}`);
  }
}
