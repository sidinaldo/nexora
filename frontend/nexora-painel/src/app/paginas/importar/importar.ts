import { Component, OnDestroy, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router, RouterLink } from '@angular/router';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { ImportacoesServico } from '../../nucleo/servicos/importacoes.servico';
import { PipelinesServico } from '../../nucleo/servicos/pipelines.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { ROTULO_ORIGEM } from '../../nucleo/rotulos';
import {
  CampoImportacao, ColunaMapeada, ImportacaoRecebida, LinhaPrevia, OrigemLead, PreviaImportacao,
  ResultadoImportacao, UsuarioEquipe
} from '../../nucleo/modelos';

/** IMPORTAR LEADS DE UM ARQUIVO (INT-XX).
 *
 *  ===================== TELA, E NÃO MODAL =====================
 *  A importação da issue #8 era um modal de dois passos, e cabia: ela não perguntava nada além do
 *  funil. Esta pergunta o que cada coluna do arquivo do cliente significa — uma tabela com uma
 *  linha por coluna, que num modal não cabe.
 *
 *  E ela ABSORVE aquele modal: duas telas de importar seriam o tipo de duplicata que este projeto
 *  passa o tempo desmontando. O CSV do Meta é um CSV com cabeçalho reconhecível, e o mapeamento
 *  generaliza os sinônimos que a #8 fixou no código.
 *  =============================================================
 *
 *  ===================== O ESTADO É O PASSO =====================
 *  `arquivo` → `mapear` → `fim`, derivados do que já chegou do servidor: sem `recebida` não há o
 *  que mapear, sem `resultado` não há o que mostrar. Um `passo: 1|2|3` seria um segundo jeito de
 *  dizer a mesma coisa, e os dois divergiriam no primeiro `catch`.
 *  ============================================================== */
@Component({
  selector: 'app-importar',
  imports: [FormsModule, RouterLink],
  templateUrl: './importar.html',
  styleUrl: './importar.css'
})
export class Importar implements OnDestroy {
  private servico = inject(ImportacoesServico);
  private equipe = inject(EquipeServico);
  private toast = inject(ToastServico);
  private router = inject(Router);
  readonly pipelines = inject(PipelinesServico);
  auth = inject(AuthServico);

  /** O rótulo de cada campo, na língua de quem lê. `ignorar` primeiro porque é o padrão de toda
   *  coluna que o servidor não reconheceu. */
  readonly campos: { campo: CampoImportacao; rotulo: string }[] = [
    { campo: 'ignorar', rotulo: '— não importar —' },
    { campo: 'nome', rotulo: 'Nome' },
    { campo: 'telefone', rotulo: 'Telefone' },
    { campo: 'email', rotulo: 'E-mail' },
    { campo: 'observacoes', rotulo: 'Observações' },
    { campo: 'origem', rotulo: 'Origem (whatsapp, indicação…)' },
    { campo: 'origem_detalhe', rotulo: 'Campanha (de onde veio)' },
    { campo: 'criado_em', rotulo: 'Data de entrada' },
    { campo: 'meta_lead_id', rotulo: 'ID do lead na Meta' },
    { campo: 'meta_ad_id', rotulo: 'ID do anúncio' },
    { campo: 'meta_campaign_id', rotulo: 'ID da campanha' },
    { campo: 'meta_form_id', rotulo: 'ID do formulário' }
  ];

  recebida = signal<ImportacaoRecebida | null>(null);
  mapeamento = signal<ColunaMapeada[]>([]);
  previa = signal<PreviaImportacao | null>(null);
  resultado = signal<ResultadoImportacao | null>(null);

  ocupado = signal(false);
  erro = signal('');

  /** Nulo = só os contatos, sem card nenhum. É o padrão: 10.000 cards de uma vez é um quadro
   *  inutilizável, e desfazer é apagar 10.000 linhas na mão. */
  funil = signal<number | null>(null);
  responsavel = signal<number | null>(null);
  avisarIntegracoes = signal(false);
  equipeLista = signal<UsuarioEquipe[]>([]);

  /** ⚠️ DE ONDE VIERAM — e esta pergunta FALTAVA. A tela gravava todo contato como `meta_ads`,
   *  fixo: a base que o cliente novo sobe no primeiro dia entrava inteira como lead de anúncio.
   *
   *  A resposta vem sugerida pelo servidor (export da Meta → `meta_ads`; planilha → `manual`), e
   *  uma coluna mapeada como "origem" manda por linha quando disser algo conhecido. */
  origem = signal<OrigemLead>('manual');

  /** O nome de cada origem, do mapa compartilhado — o mesmo que o dashboard usa. */
  readonly rotuloOrigem = ROTULO_ORIGEM;

  readonly origens: OrigemLead[] = [
    'meta_ads', 'whatsapp', 'instagram', 'facebook', 'google', 'site', 'qrcode', 'indicacao',
    'manual', 'outro'
  ];

  passo = computed<'arquivo' | 'mapear' | 'fim'>(() =>
    this.resultado() ? 'fim' : this.recebida() ? 'mapear' : 'arquivo');

  /** ⚠️ CONVENIÊNCIA DE DIGITAÇÃO, NÃO A REGRA. Quem recusa é o servidor, com a frase que explica;
   *  isto só evita o clique que já se sabe que volta erro. */
  temTelefone = computed(() => this.mapeamento().some(m => m.campo === 'telefone'));

  /** O arquivo grande é gravado por um job, e a tela pergunta de tempos em tempos onde ele está. */
  private relogio?: ReturnType<typeof setInterval>;

  ngOnDestroy() { this.pararDeAcompanhar(); }

  // ==================================================================== 1. o arquivo
  escolherArquivo(evento: Event) {
    const alvo = evento.target as HTMLInputElement;
    const arquivo = alvo.files?.[0];
    if (!arquivo || this.ocupado()) return;

    this.erro.set('');
    this.ocupado.set(true);

    this.servico.receber(arquivo).subscribe({
      next: r => {
        this.ocupado.set(false);
        this.recebida.set(r);
        this.mapeamento.set(r.mapeamento);
        this.origem.set(r.origemSugerida);
        this.previa.set(null);
        // A lista da equipe só serve ao seletor de responsável, e só quem gerencia equipe o vê.
        if (this.auth.pode('gerenciar_equipe') && this.equipeLista().length === 0) {
          this.equipe.listar().subscribe({ next: us => this.equipeLista.set(us), error: () => { } });
        }
      },
      error: e => {
        this.ocupado.set(false);
        this.erro.set(e.error?.erro ?? 'Não foi possível ler o arquivo.');
      }
    });
  }

  // ==================================================================== 2. o mapeamento
  /** Trocar uma coluna joga a prévia fora: ela descreve o mapeamento ANTERIOR, e deixá-la na tela
   *  seria oferecer "Importar 612" sobre números que já não são os daquele mapeamento. */
  ligar(coluna: string, campo: CampoImportacao) {
    this.mapeamento.update(m => m.map(x => x.coluna === coluna ? { ...x, campo } : x));
    this.previa.set(null);
  }

  conferir() {
    const r = this.recebida();
    if (!r || this.ocupado()) return;

    this.erro.set('');
    this.ocupado.set(true);

    this.servico.prever(r.id, this.mapeamento()).subscribe({
      next: p => {
        this.ocupado.set(false);
        this.previa.set(p);
        // O padrão da caixinha é do SERVIDOR: marcada no arquivo da Meta (o lead é de ontem),
        // desmarcada na planilha comum. A tela não decide.
        this.avisarIntegracoes.set(p.aviso.marcadoPorPadrao);
      },
      error: e => {
        this.ocupado.set(false);
        this.previa.set(null);
        this.erro.set(e.error?.erro ?? 'Não foi possível conferir o arquivo.');
      }
    });
  }

  // ==================================================================== 3. gravar
  importar() {
    const r = this.recebida();
    if (!r || this.ocupado()) return;

    this.erro.set('');
    this.ocupado.set(true);

    this.servico.gravar(r.id, {
      mapeamento: this.mapeamento(),
      pipelineId: this.funil(),
      responsavelId: this.responsavel(),
      avisarIntegracoes: this.avisarIntegracoes(),
      origem: this.origem()
    }).subscribe({
      next: fim => {
        this.ocupado.set(false);
        this.resultado.set(fim);
        if (fim.status === 'processando') this.acompanhar(r.id);
        else this.anunciar(fim);
      },
      error: e => {
        this.ocupado.set(false);
        this.erro.set(e.error?.erro ?? 'Não foi possível importar.');
      }
    });
  }

  /** ⚠️ O ARQUIVO GRANDE É GRAVADO POR UM JOB, e a tela pergunta a cada 2 s onde ele está. Os
   *  contadores sobem a cada lote no servidor, então o número anda — sem isso a tela ficaria em
   *  "processando" parada, e quem está olhando concluiria que travou. */
  private acompanhar(id: number) {
    this.pararDeAcompanhar();

    this.relogio = setInterval(() => {
      this.servico.acompanhar(id).subscribe({
        next: fim => {
          this.resultado.set(fim);
          if (fim.status === 'processando') return;
          this.pararDeAcompanhar();
          this.anunciar(fim);
        },
        // Uma resposta perdida não derruba o acompanhamento: a próxima pergunta vem em 2 s.
        error: () => { }
      });
    }, 2000);
  }

  private pararDeAcompanhar() {
    if (this.relogio) clearInterval(this.relogio);
    this.relogio = undefined;
  }

  private anunciar(fim: ResultadoImportacao) {
    if (fim.status === 'erro') {
      this.toast.erro('A importação parou no meio. O que já entrou está na lista de contatos.');
      return;
    }

    this.toast.sucesso(fim.importados === 0
      ? 'Nenhum contato novo: todos já estavam na base.'
      : `${fim.importados} contato${fim.importados === 1 ? '' : 's'} importado${fim.importados === 1 ? '' : 's'}.`);
  }

  // ==================================================================== apoio
  /** Recomeçar do zero — outro arquivo. */
  recomecar() {
    this.pararDeAcompanhar();
    this.recebida.set(null);
    this.mapeamento.set([]);
    this.previa.set(null);
    this.resultado.set(null);
    this.funil.set(null);
    this.responsavel.set(null);
    this.origem.set('manual');
    this.avisarIntegracoes.set(false);
    this.erro.set('');
  }

  verContatos() { this.router.navigate(['/contatos']); }

  /** O que a linha da prévia diz na coluna de situação. O motivo vem do servidor em código
   *  (`telefone_invalido`, `repetido_no_arquivo:12`) — aqui ele vira frase. */
  motivoLegivel(l: LinhaPrevia): string {
    const m = l.motivo ?? '';
    if (m.startsWith('repetido_no_arquivo:')) return `repetido: já aparece na linha ${m.split(':')[1]}`;

    return {
      telefone_invalido: 'telefone ilegível',
      telefone_ausente: 'sem telefone',
      lead_ja_importado: 'este lead já foi importado antes',
      telefone_ja_cadastrado: 'já existe um contato com este telefone'
    }[m] ?? m;
  }
}
