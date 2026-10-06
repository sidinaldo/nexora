import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';

/** ===================== LPA-1 · QUEM PAROU DE SER TRABALHADO =====================
 *
 *  ⚠️ `negociacaoId`, `pipelineNome` e `etapaNome` SÃO NULOS quando o contato nunca virou negócio
 *  — e esse é o lead frio mais comum, não uma falha de dado. A tela escreve travessão; tratar como
 *  erro esconderia justamente quem mais precisa aparecer.
 *
 *  ⚠️ A GRANULARIDADE É A NEGOCIAÇÃO ABERTA, não o contato. Uma pessoa com negócio aberto em dois
 *  funis rende duas linhas, e é o certo: os dois podem estar parados por motivos diferentes.
 *
 *  ⚠️ ESTES TIPOS NÃO SE RECOPIAM NA TELA. Redeclarar interface do serviço no componente já causou
 *  bug aqui — o `/opcoes` ganhou um campo e a cópia ficou com a forma antiga.
 *  ============================================================================================ */
export interface LeadParado {
  contatoId: number;
  nome: string;
  telefone: string;
  origem: string;
  responsavelId: number | null;
  responsavelNome: string | null;
  negociacaoId: number | null;
  pipelineNome: string | null;
  etapaNome: string | null;
  valor: number | null;
  paradoDesde: string;
  diasParado: number;
}

export interface PaginaLeadsParados {
  itens: LeadParado[];
  total: number;
}

/** As janelas que o servidor aceita. Lista fechada no `JanelasDeParada`: qualquer outro número
 *  volta 400. */
export type JanelaDeParada = 15 | 30 | 60 | 90;

@Injectable({ providedIn: 'root' })
export class LeadsParadosServico {
  private http = inject(HttpClient);

  readonly janelas: JanelaDeParada[] = [15, 30, 60, 90];

  /** Cinquenta por página porque a tela existe para AGIR em lote sobre o que está nela — vinte
   *  obrigaria a paginar no meio de uma seleção. */
  readonly porPagina = 50;

  listar(
    dias: JanelaDeParada, responsavelId: number | null, pagina: number
  ): Observable<PaginaLeadsParados> {
    let p = new HttpParams()
      .set('dias', dias)
      .set('pagina', pagina)
      .set('tamanho', this.porPagina);

    // Só o que foi preenchido entra: `!= null` e não truthy, para não perder o id 0.
    if (responsavelId != null) p = p.set('responsavelId', responsavelId);

    return this.http.get<PaginaLeadsParados>(`${API}/leads-parados`, { params: p });
  }
}
