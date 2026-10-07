import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';

/** O estado da pesquisa como o servidor o manda — o nome do enum C#, sem política de nome
 *  (`OpcoesJson`). */
export type StatusPesquisaNps =
  'Agendada' | 'Enviada' | 'Respondida' | 'PossivelNota' | 'Expirada' | 'Cancelada';

/** Uma compra e o que a pesquisa dela deu (NPS-1 3.5).
 *
 *  ⚠️ `nota` EM `PossivelNota` É SUSPEITA, não resultado: a tela lê o `status` antes de mostrar o
 *  número. Mesmo cuidado do relatório, que só conta `Respondida`. */
export interface NotaDaCompra {
  pesquisaId: number;
  negociacaoId: number;
  compraEm: string | null;
  valor: number | null;
  status: StatusPesquisaNps;
  nota: number | null;
  comentario: string | null;
  dataAgendada: string;
  dataEnvio: string | null;
  dataResposta: string | null;
}

/** "O cliente escreveu isto — é a nota X?" (NPS-1 3.6). */
export interface NotaEmDuvida {
  pesquisaId: number;
  nota: number;
  texto: string | null;
  respondidaEm: string | null;
}

/** A pesquisa pós-venda vista de um contato e de uma conversa. O relatório e a lista de respostas
 *  moram no `RelatoriosServico`, com a barra de filtros. */
@Injectable({ providedIn: 'root' })
export class PesquisaNpsServico {
  private http = inject(HttpClient);

  doContato(contatoId: number): Observable<NotaDaCompra[]> {
    return this.http.get<NotaDaCompra[]>(`${API}/pesquisas-nps/contato/${contatoId}`);
  }

  /** ⚠️ NULO É A RESPOSTA NORMAL: o servidor devolve 204 quando não há dúvida, e o `HttpClient`
   *  entrega `null` no corpo vazio. */
  emDuvida(conversaId: number): Observable<NotaEmDuvida | null> {
    return this.http.get<NotaEmDuvida | null>(`${API}/pesquisas-nps/em-duvida`, {
      params: { conversaId }
    });
  }

  /** "É a nota X." As ações da faixa correm no servidor — confirmar nota 2 cria o lembrete do
   *  detrator, igual à nota 2 lida sozinha. */
  confirmar(pesquisaId: number): Observable<void> {
    return this.http.post<void>(`${API}/pesquisas-nps/${pesquisaId}/confirmar`, {});
  }

  /** "Era outra coisa." A pesquisa volta a esperar a nota de verdade. */
  naoEhNota(pesquisaId: number): Observable<void> {
    return this.http.post<void>(`${API}/pesquisas-nps/${pesquisaId}/nao-e-nota`, {});
  }
}
