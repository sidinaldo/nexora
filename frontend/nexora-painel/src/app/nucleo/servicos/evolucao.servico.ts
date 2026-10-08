import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { API } from '../api-base';

/** ===================== EVO-1 · A EVOLUÇÃO DA CONVERSÃO =====================
 *
 *  ⚠️ `conversao` NULO NÃO É ZERO, e a tela pinta os dois diferente: `0` é um resultado ruim,
 *  `null` é a falta de resultado. Tratar nulo como zero com `?? 0` faria um mês de férias aparecer
 *  como um mês de fracasso — e é exatamente o que o relatório de vendedores faz, devolvendo `0`
 *  nos dois casos.
 *
 *  ⚠️ `amostraInsuficiente` NÃO ESCONDE O MÊS. Ele continua visível, com o volume ao lado — é o
 *  volume que explica por que o número não vale. O que a marca faz é tirá-lo da TENDÊNCIA.
 *
 *  ⚠️ ESTES TIPOS NÃO SE RECOPIAM NA TELA. Redeclarar interface do serviço no componente já causou
 *  bug aqui: o `/opcoes` passou a devolver o funil de cada etapa e a cópia ficou com a forma
 *  antiga — o campo chegava e o TypeScript afirmava que não existia.
 *  ============================================================================================ */
export interface MesDaConversao {
  ano: number;
  mes: number;
  decididos: number;
  ganhos: number;
  /** De 0 a 100, com 2 casas, PRONTO do servidor (AUD-XX); `null` quando nada foi decidido. */
  conversaoPercentual: number | null;
  /** O mês em andamento. Fica pontilhado no gráfico e fora da tendência. */
  parcial: boolean;
  /** Menos de 10 decididos: aparece esmaecido e não vota na tendência. */
  amostraInsuficiente: boolean;
}

export interface EvolucaoDoVendedor {
  /** `null` na linha "Sem dono" e na linha da equipe. */
  usuarioId: number | null;
  nome: string;
  noNexoraDesde: string | null;
  /** Meses de calendário completos desde que entrou, contados no servidor (AUD-XX). */
  mesesNoNexora: number | null;
  decididos: number;
  ganhos: number;
  /** De 0 a 100, com 2 casas, PRONTO do servidor (AUD-XX). */
  conversaoPercentual: number | null;
  /** Em PONTOS percentuais, não em porcentagem sobre a porcentagem. `null` sem tendência. */
  variacaoPontos: number | null;
  tendencia: 'melhorando' | 'piorando' | 'estavel' | 'sem_dados';
  meses: MesDaConversao[];
}

export interface EvolucaoDaEquipe {
  /** ⚠️ NULO para quem não tem `ver_numeros_da_equipe`. A média é número da equipe, e o servidor
   *  não a manda — a tela não precisa esconder nada, porque não recebe. */
  equipe: EvolucaoDoVendedor | null;
  pessoas: EvolucaoDoVendedor[];
  de: string;
  ate: string;
}

/** As três janelas que o servidor aceita. Lista fechada no `ServicoEvolucao`: qualquer outro
 *  número volta 400. */
export type JanelaEvolucao = 3 | 6 | 12;

@Injectable({ providedIn: 'root' })
export class EvolucaoServico {
  private http = inject(HttpClient);

  obter(meses: JanelaEvolucao): Observable<EvolucaoDaEquipe> {
    return this.http.get<EvolucaoDaEquipe>(`${API}/evolucao`, {
      params: new HttpParams().set('meses', meses)
    });
  }
}
