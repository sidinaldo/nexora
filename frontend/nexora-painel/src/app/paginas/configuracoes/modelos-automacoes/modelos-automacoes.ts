import { Component, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ConexaoServico } from '../../../nucleo/servicos/conexao.servico';
import { ToastServico } from '../../../nucleo/toast/toast.servico';
import { ModeloParaAutomacao } from '../../../nucleo/modelos';

/** O TEMPLATE DE CADA AUTOMAÇÃO (INT-XX).
 *
 *  Follow-up, lembrete e pesquisa saem dias depois de o cliente escrever. Pela API oficial, com a
 *  janela de 24h fechada, a Meta só aceita template aprovado — aqui o dono escolhe qual sai em cada
 *  uma. Sem escolha, a automação não sai, e a conversa mostra por quê.
 *
 *  Empresa só com conexão por QR code não vê nada daqui: lá texto livre sai a qualquer hora. A seção
 *  aparece quando há template aprovado, ou uma escolha feita. */
@Component({
  selector: 'app-modelos-automacoes',
  imports: [FormsModule],
  templateUrl: './modelos-automacoes.html',
  styleUrl: './modelos-automacoes.css'
})
export class ModelosAutomacoes {
  private servico = inject(ConexaoServico);
  private toast = inject(ToastServico);

  aprovados = signal<ModeloParaAutomacao[]>([]);
  fFollowUp = signal<number | null>(null);
  fLembrete = signal<number | null>(null);
  fNps = signal<number | null>(null);
  carregado = signal(false);
  salvando = signal(false);

  /** Há o que mostrar: template aprovado para escolher, ou uma escolha já feita. */
  visivel = computed(() => this.carregado() && (this.aprovados().length > 0
    || this.fFollowUp() !== null || this.fLembrete() !== null || this.fNps() !== null));

  constructor() {
    this.servico.modelosDasAutomacoes().subscribe({
      next: r => {
        this.aprovados.set(r.aprovados ?? []);
        this.fFollowUp.set(r.followUp ?? null);
        this.fLembrete.set(r.lembrete ?? null);
        this.fNps.set(r.nps ?? null);
        this.carregado.set(true);
      },
      // Sem a seção, as automações seguem como estão: não é motivo para erro na tela inteira.
      error: () => { }
    });
  }

  /** A escolha aponta para um template que não está mais entre os aprovados: a Meta o pausou ou
   *  recusou depois. A automação não sai até trocar. */
  foraDosAprovados(id: number | null): boolean {
    return id !== null && !this.aprovados().some(m => m.id === id);
  }

  salvar() {
    this.salvando.set(true);
    this.servico.definirModelosDasAutomacoes({
      followUp: this.fFollowUp(), lembrete: this.fLembrete(), nps: this.fNps()
    }).subscribe({
      next: () => {
        this.salvando.set(false);
        this.toast.sucesso('Templates das automações salvos.');
      },
      error: e => {
        this.salvando.set(false);
        this.toast.erro(e.error?.erro ?? 'Não foi possível salvar.');
      }
    });
  }
}
